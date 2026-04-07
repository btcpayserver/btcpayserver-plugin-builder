using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.ModelBinders;

public static class BtcPayHostVersionParser
{
    public static bool TryParse(string value, [MaybeNullWhen(false)] out PluginVersion version)
    {
        ArgumentNullException.ThrowIfNull(value);

        version = null;

        var normalized = value.Trim();
        if (normalized.Length == 0)
            return false;

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
            normalized = normalized[..buildSeparator];

        // In future consider whitelisting only -rcN suffixes instead of stripping all prerelease labels
        var prereleaseSeparator = normalized.IndexOf('-');
        if (prereleaseSeparator >= 0)
            normalized = normalized[..prereleaseSeparator];

        return PluginVersion.TryParse(normalized, out version);
    }
}

public class BtcPayHostVersionModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        var v = val.FirstValue;
        if (v is null)
            return Task.CompletedTask;

        if (BtcPayHostVersionParser.TryParse(v, out var version))
        {
            bindingContext.Result = ModelBindingResult.Success(version);
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Failed();
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid BTCPay version");
        }

        return Task.CompletedTask;
    }
}
