using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Controllers;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class UIErrorController : Controller
{
    public const string ErrorDetailsKey = "ERROR_DETAILS";
    private static readonly HashSet<int> SpecialPages = new() { 403, 404, 406, 417, 429, 500, 502 };

    [Route("/errors/{statusCode:int:range(400,599)}")]
    public IActionResult Handle(int statusCode)
    {
        if (Request.Headers.TryGetValue("Accept", out var acceptValues) &&
            acceptValues.Any(v => !string.IsNullOrEmpty(v) && v.Contains("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            if (SpecialPages.Contains(statusCode))
                return View(statusCode.ToString());

            return View("Handle", statusCode);
        }

        // Keep non-HTML responses bodyless to match BTCPay's status-only contract.
        return StatusCode(statusCode);
    }
}
