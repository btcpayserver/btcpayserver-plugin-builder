using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

[Collection("Playwright Tests")]
public class PluginDetailsUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginDetailsUITests", output);
    private class PerfMetrics
    {
        [JsonPropertyName("TTFB")]
        public double TimeToFirstByteMs { get; set; }

        [JsonPropertyName("LoadEvent")]
        public double LoadEventEndMs { get; set; }

        [JsonPropertyName("FCP")]
        public double? FirstContentfulPaintMs { get; set; }

        [JsonIgnore]
        public double VisualCompleteMs => FirstContentfulPaintMs
                                          ?? LoadEventEndMs;
    }

    [Fact]
    public async Task PluginDetails_Page_Perf_Is_OK()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var userId = await tester.Server.CreateFakeUserAsync();
        const string slug = ServerTester.PluginSlug;
        await tester.Server.CreateAndBuildPluginAsync(userId, slug: slug);

        //using 1k to avoid slow down too much CI action, but can increase for specific test
        const int reviews = 1000;
        const int votersPerReview = 50;
        const int upvoteRatioPct = 70;

        await using (var scope = tester.Server.WebApp.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<DBConnectionFactory>();
            await using var conn = await factory.Open();
            await using var tx = await conn.BeginTransactionAsync();
            await SeedReviewsAsync(tester.Server, conn, slug, reviews, votersPerReview, upvoteRatioPct, tx);
            await tx.CommitAsync();
        }

        const string url = $"/public/plugins/{slug}?sort=helpful&skip=0&count=10";

        await Context.AddInitScriptAsync("""
                                         (() => {
                                           window.__perf = { fcp: null };
                                           try {
                                             new PerformanceObserver(list => {
                                               for (const e of list.getEntries()) if (e.name === 'first-contentful-paint') window.__perf.fcp = e.startTime;
                                             }).observe({ type: 'paint', buffered: true });
                                           } catch {}
                                         })();
                                         """);

        //warmup
        await tester.GoToUrl(url);

        //now we check metrics
        await tester.GoToUrl(url);

        Debug.Assert(tester.Page != null, "tester.Page was null");

        await Expect(tester.Page.Locator("#reviews")).ToBeVisibleAsync();

        var metrics = await tester.Page.EvaluateAsync<PerfMetrics>("""
                                                            () => {
                                                                        const navs = performance.getEntriesByType('navigation');
                                                                        const nav = navs[navs.length - 1] || null;
                                                                        const fcpEntry = performance.getEntriesByName('first-contentful-paint')[0];
                                                                        return {
                                                                            TimeToFirstByteMs: nav ? nav.responseStart : 0,
                                                                            LoadEventEndMs: nav ? nav.loadEventEnd : 0,
                                                                            FirstContentfulPaintMs: fcpEntry ? fcpEntry.startTime : null,
                                                                        };
                                                                    }
                                                            """);

        Assert.True(metrics.TimeToFirstByteMs < 800, $"High TTFB: {metrics.TimeToFirstByteMs:F0} ms");
        Assert.True(metrics.VisualCompleteMs < 2000, $"High Visual (FCP/Load): {metrics.VisualCompleteMs:F0} ms");
    }

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
        Debug.Assert(tester.Page != null, "tester.Page != null");
        await Expect(tester.Page.Locator("#owner-cannot-review")).ToBeVisibleAsync();

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

    private static async Task SeedReviewsAsync(
    ServerTester tester,
    NpgsqlConnection conn,
    string slug,
    int totalReviews,
    int votersPerReview,
    int upvoteRatioPct,
    NpgsqlTransaction tx)
    {
        // 1 autor => 1 review (avoid unique (plugin_slug,user_id))
        var authorIds = new List<string>(capacity: totalReviews);
        for (var i = 0; i < totalReviews; i++)
        {
            var uid = await tester.CreateFakeUserAsync(email: $"author{i+1}@a.com", confirmEmail: true, githubVerified: true);
            authorIds.Add(uid);
        }

        var votersPoolSize = Math.Max(200, votersPerReview * 20);
        var voterIds = new List<string>(capacity: votersPoolSize);
        for (var i = 0; i < votersPoolSize; i++)
        {
            var uid = await tester.CreateFakeUserAsync(email: $"voter{i+1}@a.com", confirmEmail: true, githubVerified: true);
            voterIds.Add(uid);
        }

        var rnd = new Random(42);

        const int batch = 500;
        var written = 0;

        while (written < totalReviews)
        {
            var take = Math.Min(batch, totalReviews - written);
            var rows = new List<object>(take);

            for (var i = 0; i < take; i++)
            {
                var author = authorIds[written + i];
                var rating = rnd.Next(1, 6);
                var body = $"Great plugin #{written + i + 1}";
                var ver = new[] { 1, 0, rnd.Next(0, 8) };

                var map = new Dictionary<string, bool>();
                var attempts = 0;
                while (map.Count < votersPerReview && attempts < votersPerReview * 3)
                {
                    attempts++;
                    var voter = voterIds[rnd.Next(voterIds.Count)];
                    if (voter == author) continue;
                    var up = rnd.Next(0, 100) < upvoteRatioPct;
                    map[voter] = up;
                }
                var helpful = "{" + string.Join(",", map.Select(kv => $"\"{kv.Key}\":{kv.Value.ToString().ToLowerInvariant()}")) + "}";

                rows.Add(new
                {
                    plugin_slug = slug,
                    user_id = author,
                    rating,
                    body,
                    plugin_version = $"{{{string.Join(",", ver)}}}",
                    created_at = DateTimeOffset.UtcNow.AddMinutes(-rnd.Next(0, 60 * 24 * 30)),
                    helpful_voters = helpful
                });
            }

            await conn.ExecuteAsync("""
                INSERT INTO plugin_reviews
                  (plugin_slug, user_id, rating, body, plugin_version, created_at, helpful_voters)
                VALUES
                  (@plugin_slug, @user_id, @rating, @body, @plugin_version::int[], @created_at, @helpful_voters::jsonb);
            """, rows, tx);

            written += take;
        }
    }
}
