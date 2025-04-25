using System.Diagnostics.CodeAnalysis;

namespace PluginBuilder;

public class PluginSelector
{
    public static bool TryParse(string str, [MaybeNullWhen(false)] out PluginSelector selector)
    {
        ArgumentNullException.ThrowIfNull(str);
        selector = null;
        if (str.Length == 0)
            return false;
        if (str[0] == '[' && str[^1] == ']')
        {
            selector = new PluginSelectorByIdentifier(str[1..^1]);
            return true;
        }

        if (PluginSlug.TryParse(str, out var s))
        {
            selector = new PluginSelectorBySlug(s);
            return true;
        }

        return false;
    }
}

public class PluginSelectorByIdentifier : PluginSelector
{
    public PluginSelectorByIdentifier(string identifier)
    {
        Identifier = identifier;
    }

    public string Identifier { get; }

    public override string ToString()
    {
        return $"[{Identifier}]";
    }
}

public class PluginSelectorBySlug : PluginSelector
{
    public PluginSelectorBySlug(PluginSlug pluginSlug)
    {
        PluginSlug = pluginSlug;
    }

    public PluginSlug PluginSlug { get; }

    public override string ToString()
    {
        return PluginSlug.ToString();
    }
}
