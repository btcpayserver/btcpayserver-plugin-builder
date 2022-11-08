using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PluginBuilder
{
    public record PluginSlug
    {
        static Regex SlugRegex = new Regex("^[a-z]{1,}[a-z0-9\\-]{0,}$");
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
        public PluginSlug(string slug) : this(slug, true)
        { }
        PluginSlug(string slug, bool check)
        {
            if (check)
            {
                if (!IsValidSlugName(slug))
                    throw new ArgumentException("Invalid slug name", nameof(slug));
            }
            this.slug = slug;
        }

        
        public static implicit operator PluginSlug(string str)
        {
            return new PluginSlug(str);
        }

        readonly string slug;
        public override string ToString()
        {
            return slug;
        }
    }
}
