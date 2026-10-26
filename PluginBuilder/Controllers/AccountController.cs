using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers;

[Authorize]
[Route("/account/")]
public class AccountController(
    GPGKeyService _gpgService,
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    IdentityDbContext<IdentityUser> identityDbContext,
    IUserStore<IdentityUser> userStore,
    ExternalAccountVerificationService externalAccountVerificationService,
    GithubIdentityService githubIdentityService,
    EmailService emailService,
    NostrService nostrService,
    ILogger<AccountController> logger)
    : Controller
{
    private const int MaxAccountDetailsRequestBodyLength = PgpKeyViewModel.MaxArmouredPublicKeyLength * 4;

    [HttpGet("verifyemail")]
    public async Task<IActionResult> VerifyEmail()
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null)
            throw new Exception("User not found");

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user);

        if (needToVerifyEmail)
        {
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = Url.Action(nameof(HomeController.ConfirmEmail), "Home", new { uid = user.Id, token },
                Request.Scheme, Request.Host.ToString())!;

            var sent = await emailService.SendVerifyEmail(user.Email!, link);
            if (!sent)
            {
                TempData[TempDataConstant.WarningMessage] =
                    "We couldn't send the verification email. Please try again in a moment, or contact support if the problem persists.";
                return RedirectToAction(nameof(AccountDetails));
            }

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

        var settings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
        var isGithubVerified = await conn.IsGithubAccountVerified(user.Id);
        var isNostrVerified = !string.IsNullOrWhiteSpace(settings.Nostr?.Npub) && !string.IsNullOrWhiteSpace(settings.Nostr.Proof);

        AccountDetailsViewModel model = new()
        {
            AccountEmail = user.Email!,
            AccountEmailConfirmed = user.EmailConfirmed,
            NeedToVerifyEmail = needToVerifyEmail,
            GithubAccountVerified = isGithubVerified,
            Settings = settings,
            IsNostrVerified = isNostrVerified
        };
        return View(model);
    }

    [HttpPost("details")]
    [RequestSizeLimit(MaxAccountDetailsRequestBodyLength)]
    public async Task<IActionResult> AccountDetails(AccountDetailsViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = await userManager.GetUserAsync(User)!;

        await using var conn = await connectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();

        accountSettings.Twitter = model.Settings.Twitter;
        accountSettings.Email = model.Settings.Email;
        if (!string.IsNullOrEmpty(model.Settings.GPGKey?.PublicKey))
        {
            var isPublicKeyValid = _gpgService.ValidateArmouredPublicKey(model.Settings.GPGKey.PublicKey, out var message, out var keyViewModel);
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

    [HttpGet("2fa")]
    public async Task<IActionResult> TwoFactorAuthentication()
    {
        var user = await CurrentUser();
        return View(new TwoFactorStatusViewModel
        {
            IsEnabled = await userManager.GetTwoFactorEnabledAsync(user),
            RecoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user)
        });
    }

    [HttpGet("2fa/enable")]
    public async Task<IActionResult> EnableAuthenticator()
    {
        var user = await CurrentUserWithValidSecurityStamp();
        if (user is null)
            return Challenge();

        if (await userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(TwoFactorAuthentication));

        return await AuthenticatorSetup(user, new EnableAuthenticatorViewModel());
    }

    [HttpPost("2fa/enable")]
    public async Task<IActionResult> EnableAuthenticator(EnableAuthenticatorViewModel model)
    {
        var user = await CurrentUserWithValidSecurityStamp();
        if (user is null)
            return Challenge();

        if (await userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(TwoFactorAuthentication));
        if (!ModelState.IsValid)
            return await AuthenticatorSetup(user, model);

        // 2FA is switched on only after the app has proven it holds the key.
        var valid = await userManager.VerifyTwoFactorTokenAsync(user,
            userManager.Options.Tokens.AuthenticatorTokenProvider, TwoFactorSetup.NormalizeCode(model.Code));
        if (!valid)
        {
            ModelState.AddModelError(nameof(model.Code), "That code is not valid. Check that your device's clock is right and enter the current code.");
            return await AuthenticatorSetup(user, model);
        }

        await using var transaction = await identityDbContext.Database.BeginTransactionAsync();
        if (!(await userManager.SetTwoFactorEnabledAsync(user, true)).Succeeded)
            return TwoFactorSaveFailed();
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount);
        if (codes is null)
            return TwoFactorSaveFailed();
        await transaction.CommitAsync();
        // Enabling changes the security stamp: refresh this session so it is not signed out at the next validation.
        await signInManager.RefreshSignInAsync(user);
        logger.LogInformation("User {UserId} enabled two-factor authentication", user.Id);
        return RecoveryCodes(codes);
    }

    [HttpPost("2fa/disable")]
    public async Task<IActionResult> DisableTwoFactor(ConfirmPasswordViewModel model)
    {
        var user = await CurrentUserWithValidSecurityStamp();
        if (user is null)
            return Challenge();

        if (!await userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(TwoFactorAuthentication));
        if (!await PasswordConfirmed(user, model))
            return RedirectToAction(nameof(TwoFactorAuthentication));

        await using var transaction = await identityDbContext.Database.BeginTransactionAsync();
        if (!(await userManager.SetTwoFactorEnabledAsync(user, false)).Succeeded)
            return TwoFactorSaveFailed();
        // A new key, so the old authenticator entry is useless if 2FA is turned on again; and this browser is no
        // longer a remembered second factor.
        if (!(await userManager.ResetAuthenticatorKeyAsync(user)).Succeeded)
            return TwoFactorSaveFailed();
        await transaction.CommitAsync();
        await signInManager.ForgetTwoFactorClientAsync();
        await signInManager.RefreshSignInAsync(user);
        logger.LogInformation("User {UserId} disabled two-factor authentication", user.Id);
        TempData[TempDataConstant.SuccessMessage] = "Two-factor authentication is off.";
        return RedirectToAction(nameof(TwoFactorAuthentication));
    }

    [HttpPost("2fa/recovery-codes")]
    public async Task<IActionResult> GenerateRecoveryCodes(ConfirmPasswordViewModel model)
    {
        var user = await CurrentUserWithValidSecurityStamp();
        if (user is null)
            return Challenge();

        if (!await userManager.GetTwoFactorEnabledAsync(user))
            return RedirectToAction(nameof(TwoFactorAuthentication));
        if (!await PasswordConfirmed(user, model))
            return RedirectToAction(nameof(TwoFactorAuthentication));

        // New codes replace every earlier one.
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount);
        if (codes is null)
            return TwoFactorSaveFailed();
        logger.LogInformation("User {UserId} generated new two-factor recovery codes", user.Id);
        return RecoveryCodes(codes);
    }

    private async Task<IdentityUser> CurrentUser() =>
        await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("User not found");

    private async Task<IdentityUser?> CurrentUserWithValidSecurityStamp()
    {
        // Cookie stamp validation is periodic; exposing the authenticator key or changing 2FA needs a fresh session.
        var user = await signInManager.ValidateSecurityStampAsync(User);
        if (user is null)
            await signInManager.SignOutAsync();
        return user;
    }

    private async Task<IActionResult> AuthenticatorSetup(IdentityUser user, EnableAuthenticatorViewModel model)
    {
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            // Preparing an inactive authenticator must not invalidate sessions or API tokens.
            key = userManager.GenerateNewAuthenticatorKey();
            await ((IUserAuthenticatorKeyStore<IdentityUser>)userStore).SetAuthenticatorKeyAsync(user, key, HttpContext.RequestAborted);
            if (!(await userManager.UpdateAsync(user)).Succeeded)
                return TwoFactorSaveFailed();
        }

        // The page carries the authenticator secret, so the browser must not keep a copy of it.
        Response.Headers.CacheControl = "no-store";
        model.SharedKey = TwoFactorSetup.FormatKey(key!);
        model.AuthenticatorUri = TwoFactorSetup.AuthenticatorUri(user.Email ?? user.UserName ?? user.Id, key!);
        return View("EnableAuthenticator", model);
    }

    private IActionResult TwoFactorSaveFailed()
    {
        TempData[TempDataConstant.WarningMessage] = "Your two-factor authentication changes could not be saved. Please try again.";
        return RedirectToAction(nameof(TwoFactorAuthentication));
    }

    // Disabling 2FA or replacing the recovery codes needs the password, and a wrong one counts towards lockout.
    private async Task<bool> PasswordConfirmed(IdentityUser user, ConfirmPasswordViewModel model)
    {
        var result = string.IsNullOrEmpty(model.Password)
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signInManager.CheckPasswordSignInAsync(user, model.Password, true);
        if (result.Succeeded)
            return true;

        TempData[TempDataConstant.WarningMessage] = result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "The password is not correct.";
        return false;
    }

    // Recovery codes are shown once, in this response, and never stored anywhere the browser keeps.
    private ViewResult RecoveryCodes(IEnumerable<string> codes)
    {
        Response.Headers.CacheControl = "no-store";
        return View("ShowRecoveryCodes", new ShowRecoveryCodesViewModel { RecoveryCodes = codes.ToArray() });
    }

    [HttpGet("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount()
    {
        var user = await userManager.GetUserAsync(User);
        await using var conn = await connectionFactory.Open(HttpContext.RequestAborted);
        if (await conn.ExecuteScalarAsync<long?>("SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @id", new { id = user!.Id }) is not null)
        {
            TempData[TempDataConstant.SuccessMessage] = "GitHub account already verified";
            return RedirectToAction(nameof(AccountDetails));
        }

        await conn.ClearUnverifiedGithubHandle(user.Id);
        return View(new VerifyGitHubViewModel { Token = user.Id });
    }

    [HttpPost("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount(VerifyGitHubViewModel model)
    {
        try
        {
            var user = await userManager.GetUserAsync(User);
            model.Token = user!.Id;
            var identity = await externalAccountVerificationService.VerifyGistToken(model.GistUrl, user.Id, HttpContext.RequestAborted);
            if (identity is null)
            {
                TempData[TempDataConstant.WarningMessage] = "Unable to verify Github profile. Kindly ensure all data is correct and try again";
                return View(model);
            }

            if (!await githubIdentityService.SaveProof(user.Id, model.GistUrl, identity, HttpContext.RequestAborted))
            {
                await signInManager.SignOutAsync();
                return Forbid();
            }
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
        var challengeToken = nostrService.GetOrCreateActiveChallenge(user.Id);
        var message = $"Verifying my https://{Request.Host.Host} account. Proof: {challengeToken}";
        return Json(new StartNip07Response(challengeToken, message));
    }

    [HttpPost("nostr/verify-nip07")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> NostrVerifyNip07([FromBody] VerifyNip07Request req)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");

        if (!ModelState.IsValid)
            return BadRequest("invalid_payload");

        if (!nostrService.IsValidChallenge(user.Id, req.ChallengeToken))
            return BadRequest("invalid_or_expired_challenge");

        if (!NostrService.HasTag(req.Event, "challenge", req.ChallengeToken))
            return BadRequest("missing_challenge");

        if (!NostrService.VerifyEvent(req.Event))
            return BadRequest("invalid_signature");

        await using var conn = await connectionFactory.Open();

        var npub = NostrService.HexPubToNpub(req.Event.Pubkey);
        var npubOwnerId = await conn.GetUserIdByNpubAsync(npub);
        if (npubOwnerId is not null && !string.Equals(npubOwnerId, user.Id, StringComparison.Ordinal))
            return BadRequest("npub_already_linked_to_other_account");

        var settings = await conn.GetAccountDetailSettings(user.Id) ?? new AccountSettings();
        settings.Nostr ??= new NostrSettings();
        settings.Nostr.Npub = npub;
        settings.Nostr.Proof = req.Event.Id;

        var profile = await nostrService.GetNostrProfileByAuthorHexAsync(req.Event.Pubkey);
        if (profile is not null)
            settings.Nostr.Profile = profile;

        await conn.SetAccountDetailSettings(settings, user.Id);

        TempData[TempDataConstant.SuccessMessage] = "Nostr account verified successfully";
        return Ok();
    }

    [HttpGet("nostr/verify-public-note")]
    public async Task<IActionResult> NostrVerifyPublicNote()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");
        var token = nostrService.GetOrCreateActiveChallenge(user.Id);
        var message = $"Verifying my {Request.Host.Host} account. Proof: {token}";
        return View(new VerifyNostrManualViewModel { ChallengeToken = token, Message = message });
    }

    [HttpPost("nostr/verify-public-note")]
    public async Task<IActionResult> NostrVerifyPublicNote(VerifyNostrManualViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var user = await userManager.GetUserAsync(User) ?? throw new Exception("User not found");

        if (!nostrService.IsValidChallenge(user.Id, model.ChallengeToken))
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

        var evJson = await nostrService.FetchEventFromRelaysAsync(eventIdHex!);
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

        if (!ContentHasProof(ev.Content, model.ChallengeToken))
        {
            TempData[TempDataConstant.WarningMessage] = "Challenge token not found in note content.";
            return View(model);
        }

        await using var conn = await connectionFactory.Open();

        var npub = NostrService.HexPubToNpub(ev.Pubkey);
        var npubOwnerId = await conn.GetUserIdByNpubAsync(npub);
        if (npubOwnerId is not null && !string.Equals(npubOwnerId, user.Id, StringComparison.Ordinal))
        {
            TempData[TempDataConstant.WarningMessage] = "This Nostr Npub is already linked to another account.";
            return View(model);
        }

        var settings = await conn.GetAccountDetailSettings(user.Id) ?? new AccountSettings();
        settings.Nostr ??= new NostrSettings();
        settings.Nostr.Npub = npub;
        settings.Nostr.Proof = model.NoteRef;

        try
        {
            var profile = await nostrService.GetNostrProfileByAuthorHexAsync(ev.Pubkey);
            if (profile is not null)
                settings.Nostr.Profile = profile;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch nostr profile");
        }

        await conn.SetAccountDetailSettings(settings, user.Id);

        TempData[TempDataConstant.SuccessMessage] = "Nostr account verified successfully";
        return RedirectToAction(nameof(AccountDetails));
    }

    private static bool ContentHasProof(string? content, string token)
    {
        return !string.IsNullOrEmpty(content) && content.Contains(token);
    }
}
