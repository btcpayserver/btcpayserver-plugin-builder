using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class PluginSlugModelBinderTests
{
    [Fact]
    public async Task BindsPluginSlugFromRouteWhenFormContainsDifferentSlug()
    {
        const string routeSlug = "owned-plugin";
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["pluginSlug"] = "another-users-plugin"
        });
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(new RouteValueDictionary { ["pluginSlug"] = routeSlug }),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var valueProvider = new CompositeValueProvider
        {
            new FormValueProvider(BindingSource.Form, form, CultureInfo.InvariantCulture),
            new RouteValueProvider(BindingSource.Path, actionContext.RouteData.Values)
        };
        var metadataProvider = new EmptyModelMetadataProvider();
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(
            actionContext,
            valueProvider,
            metadataProvider.GetMetadataForType(typeof(PluginSlug)),
            bindingInfo: null,
            modelName: "pluginSlug");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["POSTGRES"] = "Host=localhost;Database=unused;Username=unused"
            })
            .Build();
        await using var factory = new DBConnectionFactory(configuration);
        var binder = new PluginSlugModelBinder(factory);

        await binder.BindModelAsync(bindingContext);

        var pluginSlug = Assert.IsType<PluginSlug>(bindingContext.Result.Model);
        Assert.Equal(routeSlug, pluginSlug.ToString());
    }
}
