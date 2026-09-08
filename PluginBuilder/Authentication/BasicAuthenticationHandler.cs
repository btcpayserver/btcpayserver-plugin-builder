using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PluginBuilder.Authentication;

public class BasicAuthenticationHandler : AuthenticationHandler<PluginBuilderAuthenticationOptions>
{
    private readonly IOptionsMonitor<IdentityOptions> _identityOptions;
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly UserManager<IdentityUser> _userManager;

    public BasicAuthenticationHandler(
        IOptionsMonitor<IdentityOptions> identityOptions,
        IOptionsMonitor<PluginBuilderAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SignInManager<IdentityUser> signInManager,
        UserManager<IdentityUser> userManager) : base(options, logger, encoder)
    {
        _identityOptions = identityOptions;
        _signInManager = signInManager;
        _userManager = userManager;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authHeader = Context.Request.Headers["Authorization"];

        if (authHeader is null || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        if (!TryParseCredentials(authHeader, out var username, out var password))
            return AuthenticateResult.Fail("Invalid Basic credentials.");

        var user = await _userManager.FindByNameAsync(username);
        if (user is null)
            return AuthenticateResult.Fail("Invalid Basic credentials.");
        // Validate each API request without issuing a persistent browser cookie.
        // CheckPasswordSignInAsync retains Identity lockout/confirmation checks.
        var result = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded || await _userManager.GetTwoFactorEnabledAsync(user))
            return AuthenticateResult.Fail("Invalid Basic credentials.");

        List<Claim> claims = new() { new Claim(_identityOptions.CurrentValue.ClaimsIdentity.UserIdClaimType, user.Id) };
        claims.AddRange((await _userManager.GetRolesAsync(user)).Select(s => new Claim(_identityOptions.CurrentValue.ClaimsIdentity.RoleClaimType, s)));

        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, PluginBuilderAuthenticationSchemes.BasicAuth)),
            PluginBuilderAuthenticationSchemes.BasicAuth));
    }

    public static bool TryParseCredentials(string header, out string username, out string password)
    {
        username = password = "";
        if (header.Length > 16384 || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header[6..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator <= 0 || separator == decoded.Length - 1)
                return false;
            username = decoded[..separator];
            password = decoded[(separator + 1)..];
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}
