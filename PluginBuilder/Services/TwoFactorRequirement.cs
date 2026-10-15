using Microsoft.AspNetCore.Identity;

namespace PluginBuilder.Services;

/// <summary>
///     Whether an account must have two-factor authentication. Optional for everyone for now; the plan is to require it
///     for builder admins first, then for publishers with listed plugins. Enforcement (redirecting such accounts to the
///     setup page) is added together with that policy; today this only feeds the account page.
/// </summary>
public class TwoFactorRequirement
{
    public Task<bool> IsRequiredAsync(IdentityUser user) => Task.FromResult(false);
}
