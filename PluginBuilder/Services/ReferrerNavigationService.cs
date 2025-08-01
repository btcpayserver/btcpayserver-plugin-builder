using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Services;

/// <summary>
/// Service for managing referrer-based navigation between actions
/// </summary>
public class ReferrerNavigationService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private const string ReferrerCookieName = "AdminReferrerUrl";

    public ReferrerNavigationService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Stores the current HTTP request's referer URL in a cookie
    /// </summary>
    public void StoreReferrer()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return;

        var referer = context.Request.Headers["Referer"].ToString();
        if (!string.IsNullOrEmpty(referer))
        {
            context.Response.Cookies.Append(ReferrerCookieName, referer, new CookieOptions
            {
                Expires = DateTime.Now.AddHours(1),
                HttpOnly = true,
                IsEssential = true
            });
        }
    }

    /// <summary>
    /// Creates a redirect result to return to the referrer page or a default action
    /// </summary>
    /// <param name="controller">The controller to use for creating the redirect result</param>
    /// <param name="defaultAction">The default action to redirect to if no referrer is found</param>
    /// <returns>A redirect result</returns>
    public IActionResult RedirectToReferrerOr(ControllerBase controller, string defaultAction)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return controller.RedirectToAction(defaultAction);

        if (context.Request.Cookies.TryGetValue(ReferrerCookieName, out string? referrerUrl) && 
            !string.IsNullOrEmpty(referrerUrl))
        {
            // Clear the cookie after use
            context.Response.Cookies.Delete(ReferrerCookieName);
            return controller.Redirect(referrerUrl);
        }

        return controller.RedirectToAction(defaultAction);
    }
}
