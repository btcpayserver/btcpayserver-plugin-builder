using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PluginBuilder.Controllers;
using PluginBuilder.Filters;
using Xunit;

namespace PluginBuilder.Tests.FilterTests;

public class UIControllerAntiforgeryTokenAttributeTests
{
    [Fact]
    public async Task OnAuthorizationAsync_WithPostUiRequest_ValidationFailure_SetsResultAndErrorDetails()
    {
        var filter = new UIControllerAntiforgeryTokenAttribute();
        var services = new ServiceCollection()
            .AddSingleton<IAntiforgery, ThrowingAntiforgery>()
            .BuildServiceProvider();

        var context = CreateContext(filter, typeof(DummyUiController), HttpMethods.Post, services);

        await filter.OnAuthorizationAsync(context);

        Assert.IsType<AntiforgeryValidationFailedResult>(context.Result);
        Assert.Equal("CSRF token validation failed.", context.HttpContext.Items[UIErrorController.ErrorDetailsKey]);
    }

    [Fact]
    public async Task OnAuthorizationAsync_WithExistingAntiforgeryFailure_AddsErrorDetails()
    {
        var filter = new UIControllerAntiforgeryTokenAttribute();
        var services = new ServiceCollection().BuildServiceProvider();

        var context = CreateContext(filter, typeof(DummyUiController), HttpMethods.Get, services);
        context.Result = new AntiforgeryValidationFailedResult();

        await filter.OnAuthorizationAsync(context);

        Assert.Equal("CSRF token validation failed.", context.HttpContext.Items[UIErrorController.ErrorDetailsKey]);
    }

    [Fact]
    public async Task OnAuthorizationAsync_WithPostApiRequest_DoesNotValidate()
    {
        var filter = new UIControllerAntiforgeryTokenAttribute();
        var services = new ServiceCollection()
            .AddSingleton<IAntiforgery, ThrowingAntiforgery>()
            .BuildServiceProvider();

        var context = CreateContext(filter, typeof(DummyApiController), HttpMethods.Post, services);

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.False(context.HttpContext.Items.ContainsKey(UIErrorController.ErrorDetailsKey));
    }

    [Fact]
    public async Task OnAuthorizationAsync_WithGetUiRequest_DoesNotValidate()
    {
        var filter = new UIControllerAntiforgeryTokenAttribute();
        var services = new ServiceCollection()
            .AddSingleton<IAntiforgery, ThrowingAntiforgery>()
            .BuildServiceProvider();

        var context = CreateContext(filter, typeof(DummyUiController), HttpMethods.Get, services);

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.False(context.HttpContext.Items.ContainsKey(UIErrorController.ErrorDetailsKey));
    }

    [Fact]
    public async Task OnAuthorizationAsync_WithIgnoreAntiforgeryPolicy_DoesNotValidate()
    {
        var filter = new UIControllerAntiforgeryTokenAttribute();
        var services = new ServiceCollection()
            .AddSingleton<IAntiforgery, ThrowingAntiforgery>()
            .BuildServiceProvider();

        var context = CreateContext(
            filter,
            typeof(DummyUiController),
            HttpMethods.Post,
            services,
            new IgnoreAntiforgeryTokenAttribute());

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.False(context.HttpContext.Items.ContainsKey(UIErrorController.ErrorDetailsKey));
    }

    private static AuthorizationFilterContext CreateContext(
        UIControllerAntiforgeryTokenAttribute filter,
        Type controllerType,
        string method,
        IServiceProvider services,
        params IFilterMetadata[] extraFilters)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.Method = method;

        var descriptor = new ControllerActionDescriptor
        {
            ActionName = "Action",
            ControllerName = controllerType.Name.Replace("Controller", string.Empty, StringComparison.Ordinal),
            ControllerTypeInfo = controllerType.GetTypeInfo()
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        List<IFilterMetadata> filters = new();
        filters.Add(filter);
        filters.AddRange(extraFilters);

        // Match MVC's ordered execution so IAntiforgeryPolicy precedence is realistic in tests.
        var orderedFilters = filters
            .Select((metadata, index) => new
            {
                Metadata = metadata,
                Order = (metadata as IOrderedFilter)?.Order ?? 0,
                Index = index
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Index)
            .Select(x => x.Metadata)
            .ToList();

        return new AuthorizationFilterContext(actionContext, orderedFilters);
    }

    private sealed class DummyUiController : Controller;

    private sealed class DummyApiController : ControllerBase;

    private sealed class ThrowingAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) => throw new AntiforgeryValidationException("Invalid CSRF token.");
    }
}
