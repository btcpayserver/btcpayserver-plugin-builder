using System.Diagnostics.CodeAnalysis;

namespace PluginBuilder
{
    public class PluginVersion
    {
        public static readonly PluginVersion Zero = new PluginVersion(new[] { 0, 0, 0, 0 });
        public PluginVersion(int[] version)
        {
            Version = string.Join('.', version);
            VersionParts = version;
        }
        public static PluginVersion Parse(string str)
        {
            if (!TryParse(str, out var v))
                throw new FormatException("Invalid version format");
            return v;
        }
        public static bool TryParse(string str, [MaybeNullWhen(false)] out PluginVersion version)
        {
            ArgumentNullException.ThrowIfNull(str);
            version = null;
            var parts = str.Split('.');
            if (parts.Length > 4)
                return false;
            int[] partsInt = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var p) || p < 0)
                    return false;
                partsInt[i] = p;
            }
            version = new PluginVersion(partsInt);
            return true;
        }

        public string Version { get; }
        public int[] VersionParts { get; }
        public override string ToString()
        {
            return Version;
        }
    }
}
