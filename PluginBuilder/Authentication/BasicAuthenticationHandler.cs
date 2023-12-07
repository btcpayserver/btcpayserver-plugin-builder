using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace PluginBuilder.Authentication
{
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
            
            var encodedUsernamePassword = authHeader.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[1].Trim();
            var decodedUsernamePassword = Encoding.UTF8.GetString(Convert.FromBase64String(encodedUsernamePassword)).Split(':');
            var username = decodedUsernamePassword[0];
            var password = decodedUsernamePassword[1];

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return AuthenticateResult.Fail("Basic authentication header was not in a correct format. (username:password encoded in base64)");
            }

            var result = await _signInManager.PasswordSignInAsync(username, password, true, true);
            if (!result.Succeeded)
                return AuthenticateResult.Fail(result.ToString());

            var user = await _userManager.Users
                .FirstOrDefaultAsync(applicationUser => applicationUser.NormalizedUserName == _userManager.NormalizeName(username));
            if (user == null)
            {
                return AuthenticateResult.Fail("User not found");
            }

            var claims = new List<Claim>
            {
                new (_identityOptions.CurrentValue.ClaimsIdentity.UserIdClaimType, user.Id)
            };
            claims.AddRange((await _userManager.GetRolesAsync(user)).Select(s => new Claim(_identityOptions.CurrentValue.ClaimsIdentity.RoleClaimType, s)));

            return AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, PluginBuilderAuthenticationSchemes.BasicAuth)),
                PluginBuilderAuthenticationSchemes.BasicAuth));
        }
    }
}
