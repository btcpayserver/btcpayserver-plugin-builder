using System.Globalization;

namespace PluginBuilder.Util;

/// <summary>
/// Throwaway inbox services, refused at registration. Listed domains cover their subdomains too, and case does not matter.
/// Embedded list based on a CC0 snapshot from https://github.com/disposable-email-domains/disposable-email-domains.
/// See docs/disposable-email-domains.md for the source revision, local corrections, license and update procedure.
/// </summary>
public static class DisposableEmailDomains
{
    // Matches the maximum email and username length in AspNetUsers.
    public const int MaxEmailLength = 256;

    public static readonly string[] Domains = LoadDomains();

    private static readonly HashSet<string> Lookup = new(Domains, StringComparer.OrdinalIgnoreCase);

    // Characters that IDNA reads as a dot between labels, as mail software does when it sends to the address.
    private static readonly char[] IdnaDots = ['\u3002', '\uFF0E', '\uFF61'];

    private static string[] LoadDomains()
    {
        using var stream = typeof(DisposableEmailDomains).Assembly
            .GetManifestResourceStream("PluginBuilder.Data.disposable_email_blocklist.conf")
            ?? throw new InvalidOperationException("The embedded disposable email domain list is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsDisposable(string? email)
    {
        // Bound the work even when IDNA conversion fails and falls back to scanning every suffix.
        if (email is null || email.Length > MaxEmailLength)
            return false;

        var at = email.LastIndexOf('@');
        if (at < 0)
            return false;

        var domain = email[(at + 1)..].Trim();
        // Map them to '.' first, so the lookup below still sees the labels when the name is not valid IDNA.
        foreach (var dot in IdnaDots)
            domain = domain.Replace(dot, '.');
        domain = domain.TrimEnd('.');
        try
        {
            // "mailinator\u3002com" and full-width letters reach the same inbox as "mailinator.com".
            domain = new IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException)
        {
            // Not a valid international domain name: check it as written.
        }

        while (domain.Length > 0)
        {
            if (Lookup.Contains(domain))
                return true;
            var dot = domain.IndexOf('.');
            if (dot < 0)
                return false;
            domain = domain[(dot + 1)..];
        }

        return false;
    }
}
