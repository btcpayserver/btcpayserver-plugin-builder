using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PluginBuilder.Authentication;

/// <summary>
///     Identity's sign-in manager plus one step Identity keeps protected: finishing a sign-in whose password was already
///     checked. Login checks the password before its email-verification gate, so signing in afterwards with
///     PasswordSignInAsync would hash the password a second time.
/// </summary>
public class PluginBuilderSignInManager(
    UserManager<IdentityUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<IdentityUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<IdentityUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<IdentityUser> confirmation)
    : SignInManager<IdentityUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    // What PasswordSignInAsync does once the password is right: a session, or for an account with two-factor
    // authentication on a browser it does not remember, Identity's two-factor user cookie and RequiresTwoFactor.
    public Task<SignInResult> SignInAfterPasswordCheckAsync(IdentityUser user, bool isPersistent) =>
        SignInOrTwoFactorAsync(user, isPersistent);
}
