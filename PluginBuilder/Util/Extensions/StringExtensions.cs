using System.Text;

namespace PluginBuilder.Util.Extensions;

public static class StringExtensions
{
    public static string Truncate(this string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? string.Empty;

        var lastSpace = value.LastIndexOf(' ', maxLength);
        var cutAt = lastSpace > 0 ? lastSpace : maxLength;
        return string.Concat(value.AsSpan(0, cutAt), "…");
    }

    public static string? StripControlCharacters(this string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Any(char.IsControl))
            return value;

        StringBuilder sanitized = new(value.Length);
        foreach (var c in value.Where(c => !char.IsControl(c)))
        {
            sanitized.Append(c);
        }

        return sanitized.ToString();
    }
}
