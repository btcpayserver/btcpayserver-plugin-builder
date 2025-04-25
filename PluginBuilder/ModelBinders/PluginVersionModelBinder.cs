using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.ModelBinders;

public class PluginVersionModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        var v = val.FirstValue;
        if (v is null)
            return Task.CompletedTask;
        if (PluginVersion.TryParse(v, out var version))
        {
            bindingContext.Result = ModelBindingResult.Success(version);
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Failed();
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin version");
        }

        return Task.CompletedTask;
    }
}
