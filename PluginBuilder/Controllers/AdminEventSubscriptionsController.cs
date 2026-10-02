using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[Authorize(Roles = Roles.ServerAdmin)]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("admin/event-subscriptions")]
public class AdminEventSubscriptionsController(AdminEventSubscriptionService subscriptions) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(new SubscriptionPage { Subscriptions = await subscriptions.List() });

    [HttpPost]
    public async Task<IActionResult> Create(SubscriptionPage model)
    {
        var creation = model.Creation;
        var eventTypes = creation.EventTypes ?? [];
        if (ModelState.IsValid && AdminEventSubscriptionService.Validate(creation.Kind, creation.Destination.Trim(), eventTypes) is { } error)
            ModelState.AddModelError(string.Empty, error);
        if (ModelState.IsValid)
        {
            var created = await subscriptions.Create(User.FindFirstValue(ClaimTypes.NameIdentifier)!, creation.Kind,
                creation.Destination.Trim(), eventTypes);
            model.Issued = new IssuedSubscription(created.Id, created.Secret);
        }
        model.Subscriptions = await subscriptions.List();
        return View("Index", model);
    }

    [HttpPost("{id:guid}/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled)
    {
        if (!await subscriptions.SetEnabled(id, enabled))
            return NotFound();
        TempData[TempDataConstant.SuccessMessage] = enabled ? "Subscription enabled." : "Subscription disabled. New events are no longer delivered to it.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!await subscriptions.Delete(id))
            return NotFound();
        TempData[TempDataConstant.SuccessMessage] = "Subscription deleted.";
        return RedirectToAction(nameof(Index));
    }

    public record IssuedSubscription(Guid Id, string? Secret);

    public class CreationModel
    {
        [Required] public string Kind { get; set; } = "webhook";
        [Required, MaxLength(2048), Display(Name = "Destination")] public string Destination { get; set; } = "";
        [MaxLength(20)] public string[]? EventTypes { get; set; }
    }

    public class SubscriptionPage
    {
        public CreationModel Creation { get; set; } = new();
        [BindNever] public IReadOnlyList<AdminEventSubscriptionService.SubscriptionInfo> Subscriptions { get; set; } = [];
        [BindNever] public IssuedSubscription? Issued { get; set; }
    }
}
