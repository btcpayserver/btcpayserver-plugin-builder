using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Components;

public class Pager : ViewComponent
{
    public IViewComponentResult Invoke(BasePagingViewModel viewModel)
    {
        return View(viewModel);
    }
}
