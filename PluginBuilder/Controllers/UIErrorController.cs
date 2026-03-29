using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Controllers;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class UIErrorController : Controller
{
    [Route("/errors/{statusCode:int}")]
    public IActionResult Handle(int statusCode)
    {
        var acceptHeader = Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var viewResult = new ViewResult { StatusCode = statusCode };
            if (statusCode is 404 or 500)
                viewResult.ViewName = statusCode.ToString();
            else
                viewResult.ViewName = statusCode.ToString();
            return viewResult;
        }

        return StatusCode(statusCode);
    }
}
