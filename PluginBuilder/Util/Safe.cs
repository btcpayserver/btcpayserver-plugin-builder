
using Ganss.Xss;
using Microsoft.AspNetCore.Html;

public static class Safe
{
    private static readonly HtmlSanitizer _htmlSanitizer = new HtmlSanitizer();

    public static IHtmlContent Raw(string value)
    {
        return new HtmlString(_htmlSanitizer.Sanitize(value ?? string.Empty));
    }

    public static IHtmlContent RawEncode(string value)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(_htmlSanitizer.Sanitize(value ?? string.Empty));
        return new HtmlString(encoded);
    }
}
