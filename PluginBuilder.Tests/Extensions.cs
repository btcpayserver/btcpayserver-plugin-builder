using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace PluginBuilder.Tests;

public static class Extensions
    {
        
        private static readonly JsonSerializerSettings JsonSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };
        public static string ToJson(this object o) => JsonConvert.SerializeObject(o, Formatting.None, JsonSettings);

        public static string NormalizeWhitespaces(this string input) =>
            string.Concat((input??"").Where(c => !char.IsWhiteSpace(c)));

        public static async Task AssertNoError(this IPage page)
        {
            var pageSource = await page.ContentAsync();
            if (pageSource.Contains("alert-danger"))
            {
                var dangerAlerts = page.Locator(".alert-danger");
                int count = await dangerAlerts.CountAsync();
                for (int i = 0; i < count; i++)
                {
                    var alert = dangerAlerts.Nth(i);
                    if (await alert.IsVisibleAsync())
                    {
                        var alertText = await alert.InnerTextAsync();
                        Assert.Fail($"No alert should be displayed, but found this on {page.Url}: {alertText}");
                    }
                }
            }
            Assert.DoesNotContain("errors", page.Url);
            var title = await page.TitleAsync();
            Assert.DoesNotContain("Error", title, StringComparison.OrdinalIgnoreCase);
        }

        public static T AssertViewModel<T>(this IActionResult result)
        {
            Assert.NotNull(result);
            var vr = Assert.IsType<ViewResult>(result);
            return Assert.IsType<T>(vr.Model);
        }
        public static async Task<T> AssertViewModelAsync<T>(this Task<IActionResult> task)
        {
            var result = await task;
            Assert.NotNull(result);
            var vr = Assert.IsType<ViewResult>(result);
            return Assert.IsType<T>(vr.Model);
        }
    }
