using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers;

[Authorize]
[Route("/account/")]
public class AccountController(
    GPGKeyService _gpgService,
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    ExternalAccountVerificationService externalAccountVerificationService,
    EmailService emailService,
    NostrService nostrService)
    : Controller
{
    [HttpGet("verifyemail")]
    public async Task<IActionResult> VerifyEmail()
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) throw new Exception("User not found");

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user);

        if (needToVerifyEmail)
        {
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = Url.Action("ConfirmEmail", "Home", new { uid = user.Id, token },
                Request.Scheme, Request.Host.ToString())!;

            await emailService.SendVerifyEmail(user.Email!, link);

            var action = nameof(HomeController.VerifyEmail);
            var ctrl = nameof(HomeController).Replace("Controller", "");
            return RedirectToAction(action, ctrl, new { email = user.Email! });
        }

        return RedirectToAction(nameof(AccountDetails));
    }

    [HttpGet("details")]
    public async Task<IActionResult> AccountDetails()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);

        var settings = await conn.GetAccountDetailSettings(user!.Id);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        var isNostrVerified = !string.IsNullOrEmpty(settings?.Nostr?.Npub) && !string.IsNullOrEmpty(settings.Nostr?.Proof);

        AccountDetailsViewModel model = new()
        {
            AccountEmail = user.Email!,
            AccountEmailConfirmed = user.EmailConfirmed,
            NeedToVerifyEmail = needToVerifyEmail,
            GithubAccountVerified = isGithubVerified,
            Settings = settings!,
            IsNostrVerified = isNostrVerified
        };
        return View(model);
    }

    [HttpPost("details")]
    public async Task<IActionResult> AccountDetails(AccountDetailsViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.GetUserAsync(User)!;

        await using var conn = await connectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();

        accountSettings.Twitter = model.Settings.Twitter;
        accountSettings.Email = model.Settings.Email;
        if (!string.IsNullOrEmpty(model.Settings.GPGKey?.PublicKey))
        {
            var isPublicKeyValid = _gpgService.ValidateArmouredPublicKey(model.Settings.GPGKey.PublicKey.Trim(), out var message, out var keyViewModel);
            if (!isPublicKeyValid)
            {
                TempData[TempDataConstant.WarningMessage] = $"GPG Key is not valid: {message}";
                return View(model);
            }
            accountSettings.GPGKey = keyViewModel;
        }
        else
        {
            accountSettings.GPGKey = null;
        }
        await conn.SetAccountDetailSettings(accountSettings, user!.Id);
        TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
        return RedirectToAction(nameof(AccountDetails));
    }

    [HttpGet("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        if (isGithubVerified)
        {
            TempData[TempDataConstant.SuccessMessage] = "GitHub account already verified";
            return RedirectToAction(nameof(AccountDetails));
        }

        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
        accountSettings.Github = null;
        await conn.SetAccountDetailSettings(accountSettings, user!.Id);

        return View(new VerifyGitHubViewModel { Token = user!.Id });
    }

    [HttpPost("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount(VerifyGitHubViewModel model)
    {
        try
        {
            var user = await userManager.GetUserAsync(User);
            var githubGistAccount = await externalAccountVerificationService.VerifyGistToken(
                model.GistUrl, user!.Id);
            if (string.IsNullOrEmpty(githubGistAccount))
            {
                TempData[TempDataConstant.WarningMessage] = "Unable to verify Github profile. Kindly ensure all data is correct and try again";
                return View(model);
            }

            await using var conn = await connectionFactory.Open();
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
            accountSettings.Github = githubGistAccount;
            await conn.SetAccountDetailSettings(accountSettings, user!.Id);

            await conn.VerifyGithubAccount(user!.Id, model.GistUrl);
            TempData[TempDataConstant.SuccessMessage] = "Github account verified successfully";
            return RedirectToAction(nameof(AccountDetails));
        }
        catch (Exception ex)
        {
            TempData[TempDataConstant.WarningMessage] = $"Unable to validate Github profile: {ex.Message}";
            return View(model);
        }
    }

    [HttpGet("nostr/nip07-payload")]
    public async Task<IActionResult> GetNip07VerificationPayload()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");
        var domain = Request.Host.Host;
        var expires = TimeSpan.FromMinutes(30);
        var challengeToken = nostrService.GetOrCreateActiveChallenge(user.Id, expires);
        var message = $"Verifying my https://{domain} account. Proof: {challengeToken}";
        return Json(new StartNip07Response(challengeToken, message));
    }

    [HttpPost("nostr/verify-nip07")]
    public async Task<IActionResult> NostrVerifyNip07([FromBody] VerifyNip07Request req)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");

        var (ok, token, expiry) = nostrService.TryParseUserChallenge(user.Id, req.ChallengeToken);
        if (!ok || expiry <= DateTimeOffset.UtcNow)
            return BadRequest("invalid_or_expired_challenge");

        if (!NostrService.HasTag(req.Event, "domain", Request.Host.Host) || !NostrService.HasTag(req.Event, "challenge", token))
            return BadRequest("missing_tags");

        if (!NostrService.VerifyEvent(req.Event))
            return BadRequest("invalid_signature");

        await using var conn = await connectionFactory.Open();
        var settings = await conn.GetAccountDetailSettings(user.Id) ?? new AccountSettings();
        settings.Nostr ??= new NostrSettings();
        settings.Nostr.Npub = NostrService.HexPubToNpub(req.Event.Pubkey);
        settings.Nostr.Proof = req.Event.Id;

        var profile = await NostrService.GetNostrProfileByAuthorHexAsync(req.Event.Pubkey, timeoutPerRelayMs: 6000);
        if (profile is not null)
            settings.Nostr.Profile = profile;

        await conn.SetAccountDetailSettings(settings, user.Id);

        TempData[TempDataConstant.SuccessMessage] = "Nostr account verified successfully";
        return Ok();
    }

    [HttpGet("NostrVerifyManual")]
    public async Task<IActionResult> NostrVerifyManual()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");
        var token  = nostrService.GetOrCreateActiveChallenge(user.Id, TimeSpan.FromMinutes(30));
        var message = $"Verifying my https://plugin-builder.btcpayserver.org account. Proof: {token}";
        return View(new VerifyNostrManualViewModel {
            Token = token,
            Message = message
        });
    }

    [HttpPost("NostrVerifyManual")]
    public async Task<IActionResult> NostrVerifyManual(VerifyNostrManualViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");

        var (ok, token, expiry) = nostrService.TryParseUserChallenge(user.Id, model.Token);
        if (!ok || expiry <= DateTimeOffset.UtcNow)
        {
            TempData[TempDataConstant.WarningMessage] = "Invalid or expired challenge. Please retry.";
            return View(model);
        }

        var refStr = NostrService.ExtractNip19(model.NoteRef);
        if (refStr is null)
        {
            TempData[TempDataConstant.WarningMessage] = "Please provide a note/nevent URL or a NIP-19 (note1…/nevent1…).";
            return View(model);
        }

        string? eventIdHex;
        if (refStr.StartsWith("note1", StringComparison.OrdinalIgnoreCase))
        {
            if (!NostrService.TryDecodeNoteToEventIdHex(refStr, out eventIdHex))
            {
                TempData[TempDataConstant.WarningMessage] = "Invalid note reference.";
                return View(model);
            }
        }
        else if (refStr.StartsWith("nevent1", StringComparison.OrdinalIgnoreCase))
        {
            if (!NostrService.TryDecodeNeventToEventIdHex(refStr, out eventIdHex))
            {
                TempData[TempDataConstant.WarningMessage] = "Invalid nevent reference.";
                return View(model);
            }
        }
        else if (NostrService.IsHex64(refStr))
        {
            eventIdHex = refStr.ToLowerInvariant();
        }
        else
        {
            TempData[TempDataConstant.WarningMessage] = "Unsupported reference format.";
            return View(model);
        }

        var evJson = await NostrService.FetchEventFromRelaysAsync(eventIdHex!);
        if (evJson is null)
        {
            TempData[TempDataConstant.WarningMessage] = "Event not found on relays.";
            return View(model);
        }

        var ev = evJson.ToObject<NostrEvent>()!;

        if (!NostrService.VerifyEvent(ev))
        {
            TempData[TempDataConstant.WarningMessage] = "Invalid Nostr signature.";
            return View(model);
        }

        if (!ContentHasProof(ev.Content, model.Token))
        {
            TempData[TempDataConstant.WarningMessage] = "Challenge token not found in note content.";
            return View(model);
        }

        await using var conn = await connectionFactory.Open();
        var settings = await conn.GetAccountDetailSettings(user.Id) ?? new AccountSettings();
        settings.Nostr ??= new NostrSettings();
        settings.Nostr.Npub = NostrService.HexPubToNpub(ev.Pubkey);
        settings.Nostr.Proof = model.NoteRef;

        try
        {
            var profile = await NostrService.GetNostrProfileByAuthorHexAsync(ev.Pubkey, timeoutPerRelayMs: 6000);
            if (profile is not null)
                settings.Nostr.Profile = profile;
        }
        catch { }

        await conn.SetAccountDetailSettings(settings, user.Id);

        TempData[TempDataConstant.SuccessMessage] = "Nostr account verified successfully";
        return RedirectToAction(nameof(AccountDetails));
    }

    private static bool ContentHasProof(string? content, string token) =>
        !string.IsNullOrEmpty(content) && content.Contains(token);
}
