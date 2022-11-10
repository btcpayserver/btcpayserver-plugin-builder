using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.ModelBinders
{
    public class PluginSlugModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ValueProviderResult val = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            string? v = val.FirstValue as string;
            if (v is null)
                return Task.CompletedTask;
            if (PluginSlug.TryParse(v, out var slug))
                bindingContext.Result = ModelBindingResult.Success(slug);
            else
            {
                bindingContext.Result = ModelBindingResult.Failed();
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, "Invalid plugin slug");
            }
            return Task.CompletedTask;
        }
    }
}
