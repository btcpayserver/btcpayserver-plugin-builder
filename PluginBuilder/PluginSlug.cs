using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PluginBuilder;

public record PluginSlug
{
    private static readonly Regex SlugRegex = new("^[a-z]{1,}[a-z0-9\\-]{0,}$");

    private readonly string slug;

    public PluginSlug(string slug) : this(slug, true)
    {
    }

    private PluginSlug(string slug, bool check)
    {
        if (check)
            if (!IsValidSlugName(slug))
                throw new ArgumentException("Invalid slug name", nameof(slug));
        this.slug = slug;
    }

    public static bool IsValidSlugName(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        if (!SlugRegex.IsMatch(slug))
            return false;
        if (slug[^1] == '-')
            return false;
        if (slug.Length > 30)
            return false;
        if (slug.Length < 4)
            return false;
        return true;
    }

    public static bool TryParse(string str, [MaybeNullWhen(false)] out PluginSlug slug)
    {
        ArgumentNullException.ThrowIfNull(str);
        if (!IsValidSlugName(str))
        {
            slug = null;
            return false;
        }

        slug = new PluginSlug(str, false);
        return true;
    }

    public static PluginSlug Parse(string str)
    {
        if (TryParse(str, out var slug))
            return slug;
        throw new FormatException("Invalid slug name");
    }


    public static implicit operator PluginSlug(string str)
    {
        return new PluginSlug(str);
    }

    public override string ToString()
    {
        return slug;
    }
}
