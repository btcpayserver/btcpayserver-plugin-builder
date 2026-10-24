using System.Globalization;

namespace PluginBuilder.Util;

/// <summary>
/// Throwaway inbox services, refused at registration. To block another one, add its domain to <see cref="Domains"/> on its own
/// line: a listed domain covers its subdomains too, and case does not matter. Every entry below is also on the community list at
/// https://github.com/disposable-email-domains/disposable-email-domains (CC0), which is a good place to check a new one.
/// </summary>
public static class DisposableEmailDomains
{
    // Matches the maximum email and username length in AspNetUsers.
    public const int MaxEmailLength = 256;

    public static readonly string[] Domains =
    [
        "10mail.org",
        "10minemail.com",
        "10minutemail.co.uk",
        "10minutemail.com",
        "10minutemail.de",
        "10minutemail.net",
        "1secmail.com",
        "1secmail.net",
        "1secmail.org",
        "20minutemail.com",
        "20minutemail.it",
        "30minutemail.com",
        "60minutemail.com",
        "anonbox.net",
        "anonymbox.com",
        "any.pink",
        "armyspy.com",
        "binkmail.com",
        "bobmail.info",
        "byom.de",
        "chammy.info",
        "chitthi.in",
        "courriel.fr.nf",
        "crazymailing.com",
        "cuvox.de",
        "dayrep.com",
        "delaeb.com",
        "devnullmail.com",
        "discard.email",
        "discardmail.com",
        "discardmail.de",
        "dispostable.com",
        "dodgeit.com",
        "dodgit.com",
        "dontreg.com",
        "dropmail.me",
        "e4ward.com",
        "einrot.com",
        "einrot.de",
        "emailfake.com",
        "emailnax.com",
        "emailondeck.com",
        "emailsensei.com",
        "emltmp.com",
        "fakeinbox.com",
        "fakemail.net",
        "fakemailgenerator.com",
        "fexbox.org",
        "fexpost.com",
        "fextemp.com",
        "filzmail.com",
        "fleckens.hu",
        "getairmail.com",
        "getnada.com",
        "grr.la",
        "guerrillamail.biz",
        "guerrillamail.com",
        "guerrillamail.de",
        "guerrillamail.info",
        "guerrillamail.net",
        "guerrillamail.org",
        "guerrillamailblock.com",
        "gustr.com",
        "harakirimail.com",
        "hikuhu.com",
        "inboxbear.com",
        "inboxkitten.com",
        "incognitomail.com",
        "incognitomail.org",
        "inpwa.com",
        "jetable.fr.nf",
        "jetable.net",
        "jetable.org",
        "jourrapide.com",
        "kashre.com",
        "kurzepost.de",
        "letthemeatspam.com",
        "lucktaro.com",
        "mail.gw",
        "mail.tm",
        "mailbox.in.ua",
        "mailcatch.com",
        "mailde.de",
        "mailde.info",
        "maildrop.cc",
        "mailexpire.com",
        "mailforspam.com",
        "mailhazard.com",
        "mailinater.com",
        "mailinator.co.uk",
        "mailinator.com",
        "mailinator.net",
        "mailinator.org",
        "mailinator.us",
        "mailinator2.com",
        "mailmetrash.com",
        "mailmoat.com",
        "mailnesia.com",
        "mailnull.com",
        "mailpoof.com",
        "mailsac.com",
        "mailtemp.info",
        "mailto.plus",
        "mailtothis.com",
        "mailzilla.com",
        "maxxspace.com",
        "merepost.com",
        "mintemail.com",
        "minuteinbox.com",
        "moakt.cc",
        "moakt.com",
        "mohmal.com",
        "moncourrier.fr.nf",
        "monemail.fr.nf",
        "monmail.fr.nf",
        "mt2015.com",
        "mytemp.email",
        "nada.email",
        "nbmbb.com",
        "notmailinator.com",
        "nowmymail.com",
        "objectmail.com",
        "pokemail.net",
        "proxymail.eu",
        "rcpt.at",
        "reallymymail.com",
        "rhyta.com",
        "rover.info",
        "safetymail.info",
        "sendspamhere.com",
        "sharklasers.com",
        "sogetthis.com",
        "spam.la",
        "spam4.me",
        "spamavert.com",
        "spambog.com",
        "spambog.de",
        "spambog.ru",
        "spambooger.com",
        "spambox.us",
        "spamex.com",
        "spamfree24.org",
        "spamherelots.com",
        "spamhereplease.com",
        "spamobox.com",
        "streetwisemail.com",
        "superrito.com",
        "suremail.info",
        "teleworm.us",
        "temp-mail.com",
        "temp-mail.org",
        "temp-mail.ru",
        "tempail.com",
        "tempemail.net",
        "tempinbox.com",
        "tempmail.de",
        "tempmail.ninja",
        "tempmail.plus",
        "tempmailer.com",
        "tempmailer.de",
        "tempmailo.com",
        "tempomail.fr",
        "temporary-mail.net",
        "tempr.email",
        "thankyou2010.com",
        "thisisnotmyrealemail.com",
        "throwawaymail.com",
        "tmail.ws",
        "tmails.net",
        "tmpeml.com",
        "tmpmail.net",
        "tmpmail.org",
        "tradermail.info",
        "trash-mail.at",
        "trash-mail.com",
        "trashmail.at",
        "trashmail.com",
        "trashmail.de",
        "trashmail.io",
        "trashmail.me",
        "trashmail.net",
        "trashmail.ws",
        "trashmailer.com",
        "trashymail.com",
        "veryrealemail.com",
        "web-library.net",
        "wegwerfmail.de",
        "wegwerfmail.net",
        "wegwerfmail.org",
        "wh4f.org",
        "wivstore.com",
        "yopmail.com",
        "yopmail.fr",
        "yopmail.net",
        "zippymail.info",
    ];

    private static readonly HashSet<string> Lookup = new(Domains, StringComparer.OrdinalIgnoreCase);

    // Characters that IDNA reads as a dot between labels, as mail software does when it sends to the address.
    private static readonly char[] IdnaDots = ['\u3002', '\uFF0E', '\uFF61'];

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
