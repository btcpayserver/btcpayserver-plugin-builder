using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.ModelBinders
{
    public class PluginSelectorModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ValueProviderResult val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            string? v = val.FirstValue as string;
            if (v is null)
                return Task.CompletedTask;
            if (PluginSelector.TryParse(v, out var s))
                bindingContext.Result = ModelBindingResult.Success(s);
            else
            {
                bindingContext.Result = ModelBindingResult.Failed();
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin selector");
            }
            return Task.CompletedTask;
        }
    }
}
