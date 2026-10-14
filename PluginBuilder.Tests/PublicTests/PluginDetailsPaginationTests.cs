using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class PluginDetailsPaginationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task ReviewPagination_NormalizesBoundsAndPreservesPageSize()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var slug = ServerTester.CreatePluginSlug();
        await tester.CreatePublishedPluginAsync(ownerId, slug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            WITH reviewers AS (
                INSERT INTO plugin_reviewers (username)
                SELECT n::text FROM generate_series(1, 501) AS n
                RETURNING id, username
            )
            INSERT INTO plugin_reviews (plugin_slug, reviewer_id, rating, body, created_at)
            SELECT @slug, id, 5, 'Pagination review ' || username,
                   NOW() - username::int * INTERVAL '1 second'
            FROM reviewers;
            """, new { slug });

        using var client = tester.CreateHttpClient();
        var cases = new (string Query, int Skip, int Count)[]
        {
            ("", 0, 10),
            ("?count=-1", 0, 1),
            ("?skip=-1", 0, 10),
            ("?count=0", 0, 1),
            ("?count=-2147483648&skip=-2147483648", 0, 1),
            ("?count=2&skip=1", 1, 2),
            ("?count=500", 0, 500),
            ("?count=2147483647", 0, 500),
            ("?skip=2147483647", int.MaxValue, 10)
        };

        foreach (var (query, skip, count) in cases)
        {
            using var response = await client.GetAsync($"/public/plugins/{slug}{query}");
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"Request {query} returned {response.StatusCode}.");
            var html = await response.Content.ReadAsStringAsync();
            var reviews = Regex.Matches(html, @"Pagination review (\d+)")
                .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            Assert.Equal(Enumerable.Range(1, 501).Skip(skip).Take(count), reviews);

            var nextLinks = Regex.Matches(html, """<a\b[^>]*\bhref="([^"]+)"[^>]*>\s*Next\s*</a>""");
            if (reviews.Length < count)
            {
                Assert.Empty(nextLinks);
                continue;
            }

            var nextLink = Assert.Single(nextLinks);
            var nextUrl = new Uri(client.BaseAddress!, WebUtility.HtmlDecode(nextLink.Groups[1].Value));
            Assert.Equal(client.BaseAddress!.Authority, nextUrl.Authority);
            Assert.Equal($"/public/plugins/{slug}", nextUrl.AbsolutePath);
            var nextQuery = QueryHelpers.ParseQuery(nextUrl.Query);
            Assert.Equal(count.ToString(), nextQuery["count"].ToString());
            Assert.Equal((skip + count).ToString(), nextQuery["skip"].ToString());

            using var nextResponse = await client.GetAsync(nextUrl);
            Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
            var nextHtml = await nextResponse.Content.ReadAsStringAsync();
            var nextReviews = Regex.Matches(nextHtml, @"Pagination review (\d+)")
                .Select(match => int.Parse(match.Groups[1].Value));
            Assert.Equal(Enumerable.Range(1, 501).Skip(skip + count).Take(count), nextReviews);
        }
    }
}
