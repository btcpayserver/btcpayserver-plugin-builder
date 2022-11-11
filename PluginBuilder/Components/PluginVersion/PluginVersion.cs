#nullable disable

using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Components.PluginVersion
{
    public class PluginVersion : ViewComponent
    {
        public Task<IViewComponentResult> InvokeAsync(PluginVersionViewModel model)
        {
            return Task.FromResult<IViewComponentResult>(View(model));
        }
    }
}
