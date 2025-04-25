#nullable disable

using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Components.StatusMessage;

public class StatusMessage : ViewComponent
{
    public Task<IViewComponentResult> InvokeAsync()
    {
        return Task.FromResult<IViewComponentResult>(View());
    }
}
