
using Ganss.Xss;
using Microsoft.AspNetCore.Html;

namespace PluginBuilder.Util;

public static class Safe
{
    private static readonly HtmlSanitizer _htmlSanitizer = new HtmlSanitizer();

    public static IHtmlContent Raw(string value)
    {
        return new HtmlString(_htmlSanitizer.Sanitize(value ?? string.Empty));
    }
}
