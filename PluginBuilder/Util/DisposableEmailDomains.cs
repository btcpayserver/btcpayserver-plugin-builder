using System.Globalization;
using System.Text.RegularExpressions;

namespace PluginBuilder.Util;

/// <summary>
/// Throwaway inbox services, refused at registration. Additional domains can be managed in Admin > Moderation.
/// Listed domains cover their subdomains too, and case does not matter.
/// Source: https://github.com/disposable-email-domains/disposable-email-domains
/// Revision: ba992a0b4c867db3ff39605696bb7de9fbb2e11f. License: https://creativecommons.org/publicdomain/zero/1.0/
/// On updates, preserve the 11 exclusions covered by DisposableEmailDomainsTests and block flashbox.5july.org instead of 5july.org.
/// Review the changes and update the checksum in those tests.
/// </summary>
public static class DisposableEmailDomains
{
    // Matches the maximum email and username length in AspNetUsers.
    public const int MaxEmailLength = 256;

    public static readonly string[] Domains = LoadDomains();

    private static readonly HashSet<string> Lookup = new(Domains, StringComparer.OrdinalIgnoreCase);

    // Characters that IDNA reads as a dot between labels, as mail software does when it sends to the address.
    private static readonly char[] IdnaDots = ['\u3002', '\uFF0E', '\uFF61'];

    private static readonly Regex BareDomain = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.CultureInvariant);

    private static string[] LoadDomains()
    {
        using var stream = typeof(DisposableEmailDomains).Assembly
            .GetManifestResourceStream("PluginBuilder.Data.disposable_email_blocklist.conf")
            ?? throw new InvalidOperationException("The embedded disposable email domain list is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static string? NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TryNormalizeDomain(value, out var domain) || domain.Length > 253)
            return null;

        domain = domain.ToLowerInvariant();
        return BareDomain.IsMatch(domain) && Uri.CheckHostName(domain) == UriHostNameType.Dns ? domain : null;
    }

    public static bool IsDisposable(string? email) =>
        GetDomainSuffixes(email).Any(Lookup.Contains);

    public static IEnumerable<string> GetDomainSuffixes(string? email)
    {
        // Bound the work even when IDNA conversion fails and falls back to scanning every suffix.
        if (email is null || email.Length > MaxEmailLength)
            yield break;

        var at = email.LastIndexOf('@');
        if (at < 0)
            yield break;

        if (TryNormalizeDomain(email[(at + 1)..], out var domain))
            domain = domain.ToLowerInvariant();

        while (domain.Length > 0)
        {
            yield return domain;
            var dot = domain.IndexOf('.');
            if (dot < 0)
                yield break;
            domain = domain[(dot + 1)..];
        }
    }

    private static bool TryNormalizeDomain(string value, out string domain)
    {
        domain = value.Trim();
        // Map them to '.' first, so the lookup below still sees the labels when the name is not valid IDNA.
        foreach (var dot in IdnaDots)
            domain = domain.Replace(dot, '.');
        domain = domain.TrimEnd('.');
        try
        {
            // "mailinator\u3002com" and full-width letters reach the same inbox as "mailinator.com".
            domain = new IdnMapping().GetAscii(domain);
            return true;
        }
        catch (ArgumentException)
        {
            // Email checks still scan the suffixes as written when IDNA conversion fails.
            return false;
        }
    }
}
