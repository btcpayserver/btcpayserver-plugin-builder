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
}
