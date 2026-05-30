using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

[Collection("Playwright Tests")]
public class PluginDetailsUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginDetailsUITests", output);

    [Fact]
    public async Task PluginDetails_NonEmbed_KeepsReviewsStackedUnderDescription()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var ownerId = await tester.Server.CreateFakeUserAsync("layout-owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = "plugin-details-layout";
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug);

        await tester.Page!.SetViewportSizeAsync(1600, 1000);
        await tester.GoToUrl($"/public/plugins/{slug}");
        await tester.AssertNoError();

        var descriptionCard = tester.Page.Locator(".card").Filter(new LocatorFilterOptions { HasText = "Description" });
        var detailsCard = tester.Page.Locator("#download-btn").Locator("xpath=ancestor::*[contains(concat(' ', normalize-space(@class), ' '), ' card ')][1]");
        var reviewsCard = tester.Page.Locator("#reviews");

        await Expect(descriptionCard).ToHaveCountAsync(1);
        await Expect(detailsCard).ToHaveCountAsync(1);
        await Expect(reviewsCard).ToHaveCountAsync(1);

        var descriptionBox = await descriptionCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Description card was not visible.");
        var detailsBox = await detailsCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Details card was not visible.");
        var reviewsBox = await reviewsCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Reviews card was not visible.");

        var reviewsGap = reviewsBox.Y - (descriptionBox.Y + descriptionBox.Height);
        Assert.InRange(reviewsGap, 0, 80);
        Assert.True(
            reviewsBox.Y < detailsBox.Y + detailsBox.Height,
            "Reviews should stack under the description column instead of waiting for the metadata card height.");
    }

    [Fact]
    public async Task PluginDetails_EmbedNarrow_KeepsVersionBeforeReviews()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var ownerId = await tester.Server.CreateFakeUserAsync("embed-layout-owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = "plugin-details-embed-layout";
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug);

        await tester.Page!.SetViewportSizeAsync(700, 1000);
        await tester.GoToUrl($"/public/plugins/{slug}?embed=1");
        await tester.AssertNoError();

        var descriptionCard = tester.Page.Locator(".plugin-details-description-card");
        var detailsCard = tester.Page.Locator(".plugin-details-metadata-card");
        var reviewsCard = tester.Page.Locator(".plugin-details-reviews-card");

        await Expect(descriptionCard).ToHaveCountAsync(1);
        await Expect(detailsCard).ToHaveCountAsync(1);
        await Expect(reviewsCard).ToHaveCountAsync(1);
        await Expect(tester.Page.Locator("#btcpay-install-plugin-btn")).ToHaveCountAsync(1);

        var descriptionBox = await descriptionCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Description card was not visible.");
        var detailsBox = await detailsCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Details card was not visible.");
        var reviewsBox = await reviewsCard.BoundingBoxAsync() ?? throw new InvalidOperationException("Reviews card was not visible.");

        Assert.True(descriptionBox.Y < detailsBox.Y, "Description should stay above metadata in embedded details.");
        Assert.True(detailsBox.Y < reviewsBox.Y, "Metadata should stay above reviews in embedded details.");
    }

    [Fact]
    public async Task PluginDetails_EmbedSelection_PreservesCompatibilityQuery()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var ownerId = await tester.Server.CreateFakeUserAsync("embed-selection-owner@x.com", confirmEmail: true, githubVerified: true);
        const string firstSlug = "embed-select-a";
        const string secondSlug = "embed-select-b";
        await tester.Server.CreateAndBuildPluginAsync(ownerId, firstSlug);
        await tester.Server.CreateAndBuildPluginAsync(ownerId, secondSlug);

        var embedOrigin = tester.ServerUri!.GetLeftPart(UriPartial.Authority);
        await tester.GoToUrl("/");
        var detailsUrl = new Uri(tester.ServerUri!, $"/public/plugins/{firstSlug}?embed=1&btcpayVersion=2.3.6&includePreRelease=true");
        await tester.Page!.SetContentAsync($"""
                                            <iframe id="details" src="{detailsUrl}"></iframe>
                                            """);

        await tester.Page.WaitForFunctionAsync("""
                                               () => document.querySelector('#details')?.contentWindow?.document?.querySelector('[data-embed-page="details"]')
                                               """);

        await tester.Page.EvaluateAsync($$"""
                                        () => document.querySelector('#details').contentWindow.postMessage({
                                            type: 'btcpay:host-context',
                                            selectedSlug: '{{secondSlug}}'
                                        }, '{{embedOrigin}}')
                                        """);

        var finalUrlHandle = await tester.Page.WaitForFunctionAsync($$"""
                                                                       () => {
                                                                           const href = document.querySelector('#details')?.contentWindow?.location?.href || '';
                                                                           return href.includes('/public/plugins/{{secondSlug}}') ? href : false;
                                                                       }
                                                                       """);
        var finalUrl = await finalUrlHandle.JsonValueAsync<string>();

        Assert.Contains($"/public/plugins/{secondSlug}", finalUrl);
        Assert.Contains("embed=1", finalUrl);
        Assert.Contains("btcpayVersion=2.3.6", finalUrl);
        Assert.Contains("includePreRelease=true", finalUrl);
    }

    [Fact]
    public async Task PluginDetails_PreReleaseInstall_ConfirmsBeforePostingEmbedInstallRequest()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var ownerId = await tester.Server.CreateFakeUserAsync("pre-release-details-owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = "plugin-details-pre-release";
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId, slug);

        await using (var conn = await tester.Server.GetService<DBConnectionFactory>().Open())
        {
            var manifestInfo = await conn.QuerySingleAsync<string>(
                "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
                new { pluginSlug = slug, buildId = fullBuildId.BuildId });
            var manifest = PluginManifest.Parse(manifestInfo);
            await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);

            await conn.ExecuteAsync(
                """
                INSERT INTO versions (plugin_slug, ver, build_id, btcpay_min_ver, btcpay_max_ver, pre_release)
                VALUES (@pluginSlug, @version, @buildId, @btcpayMinVer, @btcpayMaxVer, TRUE)
                """,
                new
                {
                    pluginSlug = slug,
                    version = PluginVersion.Parse("1.0.3.0").VersionParts,
                    buildId = fullBuildId.BuildId,
                    btcpayMinVer = manifest.BTCPayMinVersion?.VersionParts ?? PluginVersion.Zero.VersionParts,
                    btcpayMaxVer = manifest.BTCPayMaxVersion?.VersionParts
                });
        }

        var embedOrigin = tester.ServerUri!.GetLeftPart(UriPartial.Authority);
        var detailsUrl = new Uri(tester.ServerUri!, $"/public/plugins/{slug}?btcpayVersion=1.4.6.0&includePreRelease=true&embed=1&sort=helpful&count=10");

        await tester.GoToUrl("/");
        await tester.Page!.SetContentAsync($"""
                                            <iframe id="details" src="{detailsUrl}"></iframe>
                                            """);
        await tester.Page.WaitForFunctionAsync("""
                                               () => document.querySelector('#details')?.contentWindow?.document?.querySelector('[data-embed-page="details"]')
                                               """);
        await tester.Page.EvaluateAsync("""
                                        () => {
                                            window.__installMessages = [];
                                            window.addEventListener('message', event => {
                                                if (event.data?.type === 'pb:install-requested') {
                                                    window.__installMessages.push(event.data);
                                                }
                                            });
                                        }
                                        """);
        await tester.Page.EvaluateAsync($$"""
                                        () => document.querySelector('#details').contentWindow.postMessage({
                                            type: 'btcpay:host-context',
                                            colorMode: 'light'
                                        }, '{{embedOrigin}}')
                                        """);

        var frame = tester.Page.FrameLocator("#details");
        var versionButton = frame.Locator("#version-dropdown-btn");
        await Expect(versionButton).ToContainTextAsync("1.0.3");
        await Expect(frame.Locator("#btcpay-install-plugin-btn")).ToHaveTextAsync("Install in BTCPay Server");

        await frame.Locator("#btcpay-install-plugin-btn").ClickAsync();
        await Expect(frame.Locator("#pre-release-confirm-modal")).ToBeVisibleAsync();
        Assert.Equal(0, await tester.Page.EvaluateAsync<int>("() => window.__installMessages.length"));

        await frame.Locator("#pre-release-confirm-continue").ClickAsync();
        await tester.Page.WaitForFunctionAsync("() => window.__installMessages.length === 1");
        var installMessageJson = await tester.Page.EvaluateAsync<string>("() => JSON.stringify(window.__installMessages[0])");
        Assert.Contains("\"version\":\"1.0.3.0\"", installMessageJson);
        Assert.Contains("\"preRelease\":true", installMessageJson);

        await versionButton.ClickAsync();
        var versionMenu = frame.Locator("#version-dropdown-btn").Locator("xpath=following-sibling::ul[contains(@class, 'dropdown-menu')][1]");
        var releaseItem = versionMenu.Locator("a.dropdown-item").Filter(new LocatorFilterOptions { HasText = "1.0.2" });

        Assert.DoesNotContain("pre-release", await versionMenu.InnerTextAsync(), StringComparison.OrdinalIgnoreCase);

        await releaseItem.ClickAsync();
        var finalUrlHandle = await tester.Page.WaitForFunctionAsync("""
                                                                    () => {
                                                                        const href = document.querySelector('#details')?.contentWindow?.location?.href || '';
                                                                        return href.includes('version=1.0.2.0') ? href : false;
                                                                    }
                                                                    """);
        var finalUrl = await finalUrlHandle.JsonValueAsync<string>();

        Assert.Contains("btcpayVersion=1.4.6.0", finalUrl);
        Assert.Contains("includePreRelease=true", finalUrl);
        Assert.Contains("embed=1", finalUrl);
        Assert.Contains("sort=helpful", finalUrl);
        Assert.Contains("count=10", finalUrl);
    }

    [Fact]
    public async Task PluginDetails_Reviews_Flow_Works()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // plugin + 3 users (owner, reviewer, voter)
        var ownerId = await tester.Server.CreateFakeUserAsync("owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = ServerTester.PluginSlug;
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug);

        // Set up owner's social accounts
        await using (var conn = await tester.Server.GetService<DBConnectionFactory>().Open())
        {
            await conn.SetAccountDetailSettings(new AccountSettings
            {
                Github = "rockstardev",
                Twitter = "@r0ckstardev",
                Nostr = new NostrSettings
                {
                    Npub = "npub1rockstar123",
                    Proof = "proof123"
                }
            }, ownerId);
        }

        await tester.Server.CreateFakeUserAsync("reviewer@x.com", confirmEmail: true, githubVerified: true);
        await tester.Server.CreateFakeUserAsync("voter@x.com", confirmEmail: true, githubVerified: true);


        await tester.VerifyUserAccounts("reviewer@x.com", "reviewernpub1");
        await tester.VerifyUserAccounts("voter@x.com", "voternpub1");

        const string url = $"/public/plugins/{slug}";

        //Plugin owners can't create review
        await tester.LogIn("owner@x.com");

        await tester.GoToUrl(url);
        Assert.NotNull(tester.Page);
        await Expect(tester.Page.Locator("#owner-cannot-review")).ToBeVisibleAsync();

        // Verify social verification icons are displayed
        var githubVerified = tester.Page.Locator("#github-verified-icon");
        await Expect(githubVerified).ToHaveAttributeAsync("href", new Regex("github.com/rockstardev", RegexOptions.IgnoreCase));
        var githubIcon = githubVerified.Locator("svg.icon-github");
        await Expect(githubIcon).ToBeVisibleAsync();

        var nostrVerified = tester.Page.Locator("#nostr-verified-icon");
        await Expect(nostrVerified).ToHaveAttributeAsync("href", new Regex("primal.net/p/npub1rockstar123", RegexOptions.IgnoreCase));
        var nostrIcon = nostrVerified.Locator("svg.icon-nostr");
        await Expect(nostrIcon).ToBeVisibleAsync();

        // Reviewer creates review
        await tester.Logout();
        await tester.LogIn("reviewer@x.com");
        await tester.GoToUrl(url);
        await Expect(tester.Page.Locator("#reviews")).ToBeVisibleAsync();
        var form = tester.Page.Locator("#review-form");
        await Expect(form).ToBeVisibleAsync();
        await tester.Page.ClickAsync("#ratingStars .star-btn[data-value='4']");
        await tester.Page.FillAsync("textarea[name='Body']", "Amazing bro!.");
        await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Submit" }).ClickAsync();
        await tester.Page.WaitForURLAsync(new Regex("public/plugins/.+?#reviews"));
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
            .Filter(new LocatorFilterOptions
            {
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
        await tester.Page.FillAsync("textarea[name='Body']", "scam");
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

    [Fact]
    public async Task PluginDetails_Review_Markdown_Renders_Correctly()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // Setup: Create plugin and users
        var ownerId = await tester.Server.CreateFakeUserAsync("owner@x.com", confirmEmail: true, githubVerified: true);
        const string slug = ServerTester.PluginSlug;
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug);
        await tester.Server.CreateFakeUserAsync("reviewer@x.com", confirmEmail: true, githubVerified: true);
        await tester.VerifyUserAccounts("reviewer@x.com", "reviewernpub1");

        // Login as reviewer and create review with markdown
        await tester.LogIn("reviewer@x.com");
        await tester.GoToUrl($"/public/plugins/{slug}");

        var form = tester.Page!.Locator("#review-form");
        await Expect(form).ToBeVisibleAsync();
        await tester.Page.ClickAsync("#ratingStars .star-btn[data-value='5']");

        // Review text with various markdown elements
        const string reviewText =
            "This is the **best** plugin *ever*! Read more on https://x.com/r0ckstardev. As well as [markdown Google link](https://google.com)";
        await tester.Page.FillAsync("textarea[name='Body']", reviewText);
        await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Submit" }).ClickAsync();
        await tester.Page.WaitForURLAsync(new Regex("public/plugins/.+?#reviews"));

        // Get the review card body
        var reviewCard = tester.Page.Locator(".test-review-card").First;
        await Expect(reviewCard).ToBeVisibleAsync();
        var reviewBody = reviewCard.Locator(".flex-grow-1.pe-2");
        await Expect(reviewBody).ToBeVisibleAsync();

        // Test 1: Bold text renders as <strong>
        var strongElement = reviewBody.Locator("strong");
        await Expect(strongElement).ToHaveTextAsync("best");

        // Test 2: Italic text renders as <em>
        var emElement = reviewBody.Locator("em");
        await Expect(emElement).ToHaveTextAsync("ever");

        // Test 3: Plain URL is auto-linked WITHOUT trailing period
        var plainUrlLink = reviewBody.Locator("a[href='https://x.com/r0ckstardev']");
        await Expect(plainUrlLink).ToBeVisibleAsync();
        await Expect(plainUrlLink).ToHaveTextAsync("https://x.com/r0ckstardev");
        await Expect(plainUrlLink).ToHaveAttributeAsync("target", "_blank");
        await Expect(plainUrlLink).ToHaveAttributeAsync("rel", "noopener");

        // Test 4: Markdown link renders correctly
        var markdownLink = reviewBody.Locator("a[href='https://google.com']");
        await Expect(markdownLink).ToBeVisibleAsync();
        await Expect(markdownLink).ToHaveTextAsync("markdown Google link");
        await Expect(markdownLink).ToHaveAttributeAsync("target", "_blank");
        await Expect(markdownLink).ToHaveAttributeAsync("rel", "noopener");

        // Test 5: Verify the full HTML structure
        var innerHTML = await reviewBody.InnerHTMLAsync();
        Assert.Contains("<strong>best</strong>", innerHTML);
        Assert.Contains("<em>ever</em>", innerHTML);
        Assert.Contains("<a href=\"https://x.com/r0ckstardev\" target=\"_blank\" rel=\"noopener\">https://x.com/r0ckstardev</a>", innerHTML);
        Assert.Contains("<a href=\"https://google.com\" target=\"_blank\" rel=\"noopener\">markdown Google link</a>", innerHTML);

        // Test 6: Ensure trailing period is NOT part of the link
        Assert.DoesNotContain("r0ckstardev.</a>", innerHTML);
        Assert.Contains("r0ckstardev</a>.", innerHTML);
    }
}
