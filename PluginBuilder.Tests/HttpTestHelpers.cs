using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace PluginBuilder.Tests;

internal static class HttpTestHelpers
{
    public static HttpClient CreateBrowser(ServerTester tester) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    }) { BaseAddress = new Uri(tester.WebApp.Urls.First()) };

    public static async Task LogIn(HttpClient client, string email, string password)
    {
        using var page = await client.GetAsync("/login");
        page.EnsureSuccessStatusCode();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = AntiforgeryToken(await page.Content.ReadAsStringAsync())
        });
        using var response = await client.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public static string AntiforgeryToken(string html) => InputValue(html, "__RequestVerificationToken");

    public static string InputValue(string html, string name)
    {
        var input = Regex.Match(html, $"<input\\b(?=[^>]*\\bname=\"{Regex.Escape(name)}\")[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(input.Success, $"Page must contain the {name} input.");
        var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]+)\"");
        Assert.True(value.Success);
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }
}
