#nullable enable
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PluginBuilder.Controllers;

namespace PluginBuilder.Filters;

public class UIControllerAntiforgeryTokenAttribute :
    Attribute,
    IFilterMetadata,
    IAntiforgeryPolicy,
    IAsyncAuthorizationFilter,
    IAsyncAlwaysRunResultFilter,
    IOrderedFilter
{
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.Result is AntiforgeryValidationFailedResult)
        {
            AddErrorDetails(context.HttpContext);
            return;
        }

        var antiforgery = context.HttpContext.RequestServices.GetService<IAntiforgery>();
        if (
            antiforgery is not null &&
            context.IsEffectivePolicy<IAntiforgeryPolicy>(this) &&
            ShouldValidate(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException ex)
            {
                context.Result = new AntiforgeryValidationFailedResult();
                AddErrorDetails(context.HttpContext, ex.Message);
            }
        }
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is AntiforgeryValidationFailedResult)
            AddErrorDetails(context.HttpContext);

        await next();
    }

    private static void AddErrorDetails(HttpContext context, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            context.Items[UIErrorController.ErrorDetailsKey] = message;
            return;
        }

        if (context.Items.TryGetValue(UIErrorController.ErrorDetailsKey, out var existing) &&
            existing is string existingMessage &&
            !string.IsNullOrWhiteSpace(existingMessage))
            return;

        context.Items[UIErrorController.ErrorDetailsKey] = "CSRF token validation failed.";
    }

    private static bool ShouldValidate(AuthorizationFilterContext context)
    {
        var isUi = IsUi(context);
        if (isUi is false)
            return false;

        var method = context.HttpContext.Request.Method;
        return !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsTrace(method) && !HttpMethods.IsOptions(method);
    }

    private static bool? IsUi(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor controllerActionDescriptor)
            return null;

        if (controllerActionDescriptor.ControllerName.StartsWith("UI", StringComparison.OrdinalIgnoreCase))
            return true;

        if (controllerActionDescriptor.ControllerName.StartsWith("Greenfield", StringComparison.OrdinalIgnoreCase))
            return false;

        return typeof(Controller).IsAssignableFrom(controllerActionDescriptor.ControllerTypeInfo);
    }
}
