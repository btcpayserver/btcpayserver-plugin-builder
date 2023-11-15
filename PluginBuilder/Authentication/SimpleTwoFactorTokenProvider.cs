using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace PluginBuilder.Authentication
{
    public class SimpleTwoFactorTokenProvider<TUser> : IUserTwoFactorTokenProvider<TUser> where TUser : IdentityUser
    {
        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<TUser> manager, TUser user)
        {
            if (manager != null && user != null)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        // Genereates a simple token based on the user id, email and another string.
        private string GenerateToken(IdentityUser user, string purpose)
        {
            string secretString = "coffeIsGood" + user.Email + purpose + user.Id + user.PasswordHash;
            return WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.UTF8.GetBytes(secretString))
            );
        }

        public Task<string> GenerateAsync(string purpose, UserManager<TUser> manager, TUser user)
        {
            return Task.FromResult(GenerateToken(user, purpose));
        }

        public Task<bool> ValidateAsync(string purpose, string token, UserManager<TUser> manager, TUser user)
        {
            return Task.FromResult(token == GenerateToken(user, purpose));
        }
    }
}
