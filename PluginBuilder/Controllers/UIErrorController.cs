using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Controllers;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class UIErrorController : Controller
{
    [Route("/errors/{statusCode:int}")]
    public IActionResult Handle(int? statusCode = null)
    {
        var acceptHeader = Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            if (statusCode is 404 or 500)
                return View(statusCode.ToString());

            return View(statusCode);
        }

        return StatusCode(statusCode ?? StatusCodes.Status500InternalServerError);
    }
}
