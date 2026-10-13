using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PluginBuilder.HostedServices;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class AccountPageLayoutTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    // A page wrapped in its layout twice loads the light theme stylesheet again after the dark one, so the dark theme is lost.
    [Fact]
    public async Task EachAccountPageIsASingleHtmlDocument()
    {
        await using var tester = Create();
        // These pages need only the database: drop the other hosted services (storage, builds), as the admin API tests do.
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        var client = tester.CreateHttpClient();

        foreach (var url in new[] { "/VerifyEmail?email=someone%40example.org", "/login", "/register", "/forgotpassword" })
        {
            using var response = await client.GetAsync(url);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(Count(html, "<html\\b") == 1, $"{url} rendered {Count(html, "<html\\b")} <html> elements");
            Assert.True(Count(html, "id=\"DarkThemeLinkTag\"") == 1, $"{url} rendered {Count(html, "id=\"DarkThemeLinkTag\"")} dark theme links");
        }

        var verify = await client.GetStringAsync("/VerifyEmail?email=someone%40example.org");
        Assert.Contains("<title>Verify Email</title>", verify);
        Assert.Contains("<b>someone@example.org</b>", verify);
    }

    private static int Count(string html, string pattern) => Regex.Matches(html, pattern, RegexOptions.IgnoreCase).Count;
}
