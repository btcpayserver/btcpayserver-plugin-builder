using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Components.Icon
{
    public class Icon : ViewComponent
    {
        public IViewComponentResult Invoke(string symbol)
        {
            var vm = new IconViewModel
            {
                Symbol = symbol
            };
            return View(vm);
        }
    }
}
