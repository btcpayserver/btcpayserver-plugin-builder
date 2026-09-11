using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json.Linq;
using PluginBuilder.Authentication;
using PluginBuilder.Controllers;
using Xunit;

namespace PluginBuilder.Tests;

public class AdminApiDocumentationTests
{
    [Fact]
    public void EveryAdminApiRouteIsDocumentedWithItsAuthenticationSchemes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "btcpayserver-plugin-builder.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var spec = JObject.Parse(File.ReadAllText(Path.Combine(root.FullName, "PluginBuilder/wwwroot/swagger/v1/swagger.json")));
        foreach (var controller in new[] { typeof(AdminEventsController), typeof(AdminReviewController), typeof(AdminAccessTokensController) })
        {
            var route = controller.GetCustomAttribute<RouteAttribute>()!.Template;
            var auth = controller.GetCustomAttribute<AuthorizeAttribute>()!;
            Assert.Equal("ServerAdmin", auth.Roles);
            foreach (var action in controller.GetMethods().Where(m => m.DeclaringType == controller))
            foreach (var attribute in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
                var path = "/" + (route + "/" + attribute.Template).TrimEnd('/')["api/v1/".Length..];
                path = Regex.Replace(path, @"\{(\w+):[^}]+\}", "{$1}");
                foreach (var method in attribute.HttpMethods)
                {
                    var operation = spec["paths"]![path]?[method.ToLowerInvariant()];
                    Assert.True(operation is not null, $"Missing OpenAPI operation: {method} {path}");
                    var security = Assert.IsType<JArray>(operation!["security"]);
                    var schemes = auth.AuthenticationSchemes!.Contains(PluginBuilderAuthenticationSchemes.AdminToken)
                        ? new[] { "Basic", "AdminToken" }
                        : new[] { "Basic" };
                    Assert.Equal(schemes.Length, security.Count);
                    foreach (var scheme in schemes)
                    {
                        // Separate objects mean OR; combined or empty objects change the access contract.
                        var requirement = Assert.Single(security.OfType<JObject>(), r => r.ContainsKey(scheme));
                        Assert.Single(requirement.Properties());
                        Assert.Empty(Assert.IsType<JArray>(requirement[scheme]));
                        Assert.NotNull(spec["components"]!["securitySchemes"]![scheme]);
                    }
                }
            }
        }
    }
}
