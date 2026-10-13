using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers;
using PluginBuilder.Util;
using Xunit;

namespace PluginBuilder.Tests;

// The OpenAPI document is written by hand, so these tests keep it in step with the publisher and public API.
public class PublisherApiDocumentationTests
{
    private static readonly JObject Spec = LoadSpec();

    [Fact]
    public void EveryPublicAndPublisherRouteIsDocumentedWithItsAuthentication()
    {
        var actions = typeof(ApiController).GetMethods().Where(m => m.DeclaringType == typeof(ApiController)).ToArray();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            if (action.GetCustomAttribute<ApiExplorerSettingsAttribute>()?.IgnoreApi == true)
                continue;
            foreach (var attribute in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                var path = "/" + Regex.Replace(attribute.Template!, @"\{(\w+):[^}]+\}", "{$1}");
                foreach (var method in attribute.HttpMethods)
                {
                    var operation = Spec["paths"]![path]?[method.ToLowerInvariant()];
                    Assert.True(operation is not null, $"Missing OpenAPI operation: {method} {path}");
                    if (action.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                    {
                        Assert.True(operation!["security"] is null, $"{method} {path} is anonymous but documents authentication");
                        continue;
                    }

                    // Everything else needs Basic auth, which the controller requires for all its routes.
                    var requirement = Assert.Single(Assert.IsType<JArray>(operation!["security"]).OfType<JObject>());
                    Assert.Equal("Basic", Assert.Single(requirement.Properties()).Name);
                }
            }
        }
    }

    [Fact]
    public void DocumentedFieldsAreTheOnesTheServerSends()
    {
        // API responses use MVC's Newtonsoft settings (camelCase): BTCPayMinVersion is sent as btcPayMinVersion.
        var api = JsonSerializer.Create(new MvcNewtonsoftJsonOptions().SerializerSettings);
        AssertSchema("BuildData", JObject.FromObject(new BuildData(), api));
        AssertSchema("BuildInfo", JObject.FromObject(new BuildInfo(), api));
        AssertSchema("PluginManifest", JObject.FromObject(new PluginManifest(), api));
        AssertSchema("PluginDependency", JObject.FromObject(new PluginManifest.PluginDependency(), api));
        AssertSchema("PublishedVersion", JObject.FromObject(new PublishedVersion(), api));
        AssertSchema("CreateBuildRequest", JObject.FromObject(new CreateBuildRequest(), api));
        AssertSchema("InstalledPluginRequest", JObject.FromObject(new InstalledPluginRequest("", ""), api));
        AssertSchema("ReleaseVersionRequest", JObject.FromObject(new ReleaseVersionRequest(), api));

        // Published versions return the manifest as the build stored it, with its own (PascalCase) names.
        var stored = JObject.Parse(new PluginManifest { Dependencies = [new PluginManifest.PluginDependency()] }.ToString());
        AssertSchema("PublishedManifest", stored);
        AssertSchema("PublishedDependency", (JObject)stored["Dependencies"]![0]!);
    }

    [Fact]
    public void EveryReferenceResolves()
    {
        var schemas = (JObject)Spec["components"]!["schemas"]!;
        var refs = Spec.Descendants().OfType<JProperty>().Where(p => p.Name == "$ref").Select(p => p.Value.ToString()).Distinct().ToArray();
        Assert.NotEmpty(refs);
        foreach (var reference in refs)
        {
            Assert.StartsWith("#/components/schemas/", reference);
            Assert.True(schemas[reference["#/components/schemas/".Length..]] is not null, $"Unresolved reference: {reference}");
        }
    }

    private static void AssertSchema(string schema, JObject sent)
    {
        var documented = ((JObject)Spec["components"]!["schemas"]![schema]!["properties"]!).Properties().Select(p => p.Name).Order().ToArray();
        Assert.True(documented.SequenceEqual(sent.Properties().Select(p => p.Name).Order()),
            $"{schema} documents [{string.Join(", ", documented)}] but the server sends [{string.Join(", ", sent.Properties().Select(p => p.Name).Order())}]");
    }

    private static JObject LoadSpec()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "btcpayserver-plugin-builder.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return JObject.Parse(File.ReadAllText(Path.Combine(root.FullName, "PluginBuilder/wwwroot/swagger/v1/swagger.json")));
    }
}
