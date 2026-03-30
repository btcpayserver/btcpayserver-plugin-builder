using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace PluginBuilder.Controllers;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class UIErrorController : Controller
{
    [Route("/errors/{statusCode:int:range(400,599)}")]
    public IActionResult Handle(int statusCode)
    {
        var acceptHeader = Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var viewResult = new ViewResult { StatusCode = statusCode };
            viewResult.ViewName = statusCode is 404 or 500 ? statusCode.ToString() : "Handle";
            if (viewResult.ViewName == "Handle")
                viewResult.ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary<int?>(
                    metadataProvider: new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(),
                    modelState: ModelState)
                {
                    Model = statusCode
                };
            return viewResult;
        }

        return StatusCode(statusCode, new
        {
            status = statusCode,
            title = ReasonPhrases.GetReasonPhrase(statusCode)
        });
    }
}
