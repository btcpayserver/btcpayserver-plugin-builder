using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.ModelBinders
{
    public class PluginVersionModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ValueProviderResult val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            string? v = val.FirstValue as string;
            if (v is null)
                return Task.CompletedTask;
            if (PluginVersion.TryParse(v, out var version))
                bindingContext.Result = ModelBindingResult.Success(version);
            else
            {
                bindingContext.Result = ModelBindingResult.Failed();
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin version");
            }
            return Task.CompletedTask;
        }
    }
}
