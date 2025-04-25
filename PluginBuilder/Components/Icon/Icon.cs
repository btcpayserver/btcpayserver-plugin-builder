using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Components.Icon;

public class Icon : ViewComponent
{
    public IViewComponentResult Invoke(string symbol)
    {
        IconViewModel vm = new() { Symbol = symbol };
        return View(vm);
    }
}
