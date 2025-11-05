using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

[Collection("Playwright Tests")]
public class PluginDetailsUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginDetailsUITests", output);

    [Fact]
    public async Task PluginDetails_Reviews_Flow_Works()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // plugin + 3 users (owner, reviewer, voter)
        var ownerId = await tester.Server.CreateFakeUserAsync(email: "owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = ServerTester.PluginSlug;
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug: slug);

        await tester.Server.CreateFakeUserAsync(email: "reviewer@x.com", confirmEmail: true, githubVerified: true);
        await tester.Server.CreateFakeUserAsync(email: "voter@x.com",    confirmEmail: true, githubVerified: true);

        const string url = $"/public/plugins/{slug}";

        //Plugin owners can't create review
        await tester.LogIn("owner@x.com");

        await tester.GoToUrl(url);
        Assert.NotNull(tester.Page);
        await Expect(tester.Page.Locator("#owner-cannot-review")).ToBeVisibleAsync();

        // Reviewer creates review
        await tester.Logout();
        await tester.LogIn("reviewer@x.com");
        await tester.GoToUrl(url);
        await Expect(tester.Page.Locator("#reviews")).ToBeVisibleAsync();
        var form = tester.Page.Locator("#review-form");
        await Expect(form).ToBeVisibleAsync();
        await tester.Page.ClickAsync("#ratingStars .star-btn[data-value='4']");
        await Expect(tester.Page.Locator("div.note-editable")).ToBeVisibleAsync();
        await tester.Page.FillAsync("div.note-editable", "Amazing bro!.");
        await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Submit" }).ClickAsync();
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        var firstCard = tester.Page.Locator(".test-review-card").First;
        await Expect(firstCard).ToBeVisibleAsync();
        var ratingElement = firstCard.Locator(".test-review-rating");
        var ratingValueStr = await ratingElement.GetAttributeAsync("data-rating");
        Assert.True(int.TryParse(ratingValueStr, out var ratingValue), $"invalid data-rating: '{ratingValueStr}'");
        Assert.True(ratingValue == 4, $"Expected = 4, but was {ratingValue}");

        // Review Owner can't vote
        var upvoteCount = firstCard.Locator(".test-upvote-disabled");
        var downvoteCount = firstCard.Locator(".test-downvote-disabled");
        await Expect(upvoteCount).ToBeVisibleAsync();
        await Expect(downvoteCount).ToBeVisibleAsync();
        await Expect(firstCard.Locator("form[action*='VoteReview'] button[type='submit']")).ToHaveCountAsync(0);

        // Regular user can vote
        await tester.Logout();
        await tester.LogIn("voter@x.com");
        await tester.GoToUrl(url);

        var votableCard = tester.Page
            .Locator(".test-review-card")
            .Filter(new LocatorFilterOptions {
                Has = tester.Page.Locator(".test-upvote-form")
            })
            .First;

        var upSel = votableCard.Locator(".test-upvote-form");
        var upBeforeText = (await upSel.InnerTextAsync()).Trim();
        var upBefore = int.TryParse(upBeforeText, out var n1) ? n1 : 0;
        await votableCard.Locator(".test-upvote-form").ClickAsync();
        await tester.Page.WaitForURLAsync(new Regex("public/plugins/.+?#reviews"));
        var upAfterText = (await votableCard
            .Locator(".test-upvote-form")
            .InnerTextAsync()).Trim();
        var upAfter = int.TryParse(upAfterText, out var n2) ? n2 : 0;
        Assert.Equal(upBefore + 1, upAfter);

        // remove helpful vote
        await votableCard.Locator(".test-upvote-form").ClickAsync();
        await tester.Page.WaitForURLAsync(new Regex("public/plugins/.+?#reviews"));

        var upAfterToggleText = (await votableCard
            .Locator(".test-upvote-form")
            .InnerTextAsync()).Trim();
        var upAfterToggle = int.TryParse(upAfterToggleText, out var n3) ? n3 : 0;
        Assert.Equal(upBefore, upAfterToggle);

        //filter rating
        await tester.Logout();
        await tester.LogIn("voter@x.com");
        await tester.GoToUrl(url);

        // add 1 more review with 1 star
        await Expect(tester.Page!.Locator("#review-form")).ToBeVisibleAsync();
        await tester.Page.ClickAsync("#ratingStars .star-btn[data-value='1']");
        await Expect(tester.Page.Locator("div.note-editable")).ToBeVisibleAsync();
        await tester.Page.FillAsync("div.note-editable", "scam");
        await tester.Page.Locator("#review-form").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Submit" }).ClickAsync();
        await tester.Page.WaitForURLAsync(new Regex("public/plugins/.+?#reviews"));

        //check if filter works
        await tester.Page.ClickAsync("a[href*='RatingFilter=4'][href*='#reviews']");
        await tester.Page.WaitForURLAsync(new Regex(@"[?&]RatingFilter=4(\D|$)"));
        await Expect(tester.Page.Locator("#rating-filter-badge")).ToContainTextAsync("4");
        var cards = tester.Page.Locator(".test-review-card");
        var cardCount = await cards.CountAsync();
        Assert.Equal(1, cardCount);
        for (var i = 0; i < cardCount; i++)
        {
            var ratingEl = cards.Nth(i).Locator(".test-review-rating");
            await Expect(ratingEl).ToBeVisibleAsync();

            var ratingAttr = await ratingEl.GetAttributeAsync("data-rating");
            Assert.True(int.TryParse(ratingAttr, out var rating), $"invalid data-rating: '{ratingAttr}'");
            Assert.True(rating == 4, $"Expected = 4, but was {rating}");
        }
    }
}
