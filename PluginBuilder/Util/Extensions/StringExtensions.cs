using System.Text;

namespace PluginBuilder.Util.Extensions;

public static class StringExtensions
{
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
