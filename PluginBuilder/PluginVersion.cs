using System.Diagnostics.CodeAnalysis;

namespace PluginBuilder;

public class PluginVersion : IComparable<PluginVersion>
{
    public static readonly PluginVersion Zero = new(new[] { 0, 0, 0, 0 });

    public PluginVersion(int[] version)
    {
        Version = string.Join('.', version);
        VersionParts = version;
    }

    public string Version { get; }
    public int[] VersionParts { get; }

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
        var partsInt = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var p) || p < 0)
                return false;
            partsInt[i] = p;
        }

        version = new PluginVersion(partsInt);
        return true;
    }

    public override string ToString()
    {
        return Version;
    }

    public int CompareTo(PluginVersion? other)
    {
        if (other is null)
            return 1;

        var maxParts = Math.Max(VersionParts.Length, other.VersionParts.Length);
        for (var i = 0; i < maxParts; i++)
        {
            var left = VersionParts.ElementAtOrDefault(i);
            var right = other.VersionParts.ElementAtOrDefault(i);
            var cmp = left.CompareTo(right);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    public bool IsAtLeast(int major, int minor)
    {
        var actualMajor = VersionParts.ElementAtOrDefault(0);
        var actualMinor = VersionParts.ElementAtOrDefault(1);

        return actualMajor > major || (actualMajor == major && actualMinor >= minor);
    }
}
