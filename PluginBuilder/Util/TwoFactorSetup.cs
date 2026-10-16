using System.Text;

namespace PluginBuilder.Util;

/// <summary>
///     Formatting for authenticator-app (TOTP) two-factor authentication, on top of ASP.NET Core Identity's
///     authenticator token provider.
/// </summary>
public static class TwoFactorSetup
{
    public const string Issuer = "BTCPay Plugin Builder";
    public const int RecoveryCodeCount = 10;

    /// <summary>The shared key in lower-case groups of four, the way authenticator apps show it for manual entry.</summary>
    public static string FormatKey(string unformattedKey)
    {
        var result = new StringBuilder();
        for (var i = 0; i < unformattedKey.Length; i += 4)
        {
            if (result.Length > 0)
                result.Append(' ');
            result.Append(unformattedKey.AsSpan(i, Math.Min(4, unformattedKey.Length - i)));
        }

        return result.ToString().ToLowerInvariant();
    }

    /// <summary>The otpauth:// URI an authenticator app imports (Key Uri Format).</summary>
    public static string AuthenticatorUri(string accountName, string unformattedKey) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(accountName)}" +
        $"?secret={unformattedKey}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";

    /// <summary>An authenticator code as typed: apps show it with a space or dash in the middle.</summary>
    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();

    /// <summary>
    ///     A recovery code as typed. Identity stores the codes with their dash (XXXXX-XXXXX) and redeems an exact match,
    ///     so only spaces go and the case is restored.
    /// </summary>
    public static string NormalizeRecoveryCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
}
