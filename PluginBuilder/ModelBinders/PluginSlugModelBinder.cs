using Microsoft.AspNetCore.Mvc.ModelBinding;
using PluginBuilder.Extensions;
using PluginBuilder.Services;

namespace PluginBuilder.ModelBinders;

public class PluginSlugModelBinder : IModelBinder
{
    private readonly DBConnectionFactory _connectionFactory;

    public PluginSlugModelBinder(DBConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        var v = val.FirstValue;
        if (v is null)
            return;
        if (PluginSelector.TryParse(v, out var s))
        {
            var pluginSlug = await _connectionFactory.ResolvePluginSlug(s);
            if (pluginSlug != null)
            {
                bindingContext.Result = ModelBindingResult.Success(pluginSlug);
            }
            else
            {
                bindingContext.Result = ModelBindingResult.Failed();
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Unknown plugin identifier");
            }
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Failed();
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin selector");
        }
    }
}
