using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminOwnerEmailPaginationUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminOwnerEmailPaginationUITests", output);

    [Fact]
    public async Task EmailPrimaryOwners_Opens_Composer_With_A_Single_Connection_Pool()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
            services.Replace(ServiceDescriptor.Singleton<DBConnectionFactory>(provider =>
            {
                var connectionString = new NpgsqlConnectionStringBuilder(
                    provider.GetRequiredService<IConfiguration>().GetRequired("POSTGRES"))
                {
                    MaxPoolSize = 1,
                    Timeout = 2
                };
                return new DBConnectionFactory(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["POSTGRES"] = connectionString.ToString()
                    }).Build());
            }));
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        const string ownerEmail = "single-pool-owner@example.com";
        var owner = await tester.CreateConfirmedUser(ownerEmail);
        await using (var conn = await tester.Server.GetService<DBConnectionFactory>().Open())
            await AddPlugin(conn, "single-pool-plugin", owner);

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl("/admin/plugins/email-primary-owners");
        await tester.AssertNoError();
        await AssertRecipients(tester.Page!, ownerEmail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(20)]
    public async Task EmailPrimaryOwners_Includes_All_Matching_Owners_Regardless_Of_Page(int skip)
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        const string firstEmail = "first-owner@example.com";
        const string secondEmail = "second-owner@example.com";
        var firstOwner = await tester.CreateConfirmedUser(firstEmail);
        var secondOwner = await tester.CreateConfirmedUser(secondEmail);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await AddPlugin(conn, "email-first", firstOwner);
        await AddPlugin(conn, "email-second", firstOwner);
        await AddPlugin(conn, "email-third", secondOwner);

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await OpenRecipients(tester, $"Count=2&Skip={skip}");
        await AssertRecipients(tester.Page!, firstEmail, secondEmail);
    }

    [Fact]
    public async Task EmailPrimaryOwners_Preserves_Search_Status_And_Latest_Release_Filters()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var fixtures = new[]
        {
            (Slug: "matching-slug", Email: "slug-owner@example.com", Title: "One", Status: "listed", Release: (bool?)false),
            (Slug: "title-match", Email: "title-owner@example.com", Title: "Matching title", Status: "listed", Release: (bool?)false),
            (Slug: "email-match", Email: "matching-owner@example.com", Title: "Three", Status: "listed", Release: (bool?)false),
            (Slug: "hidden-match", Email: "hidden-owner@example.com", Title: "Matching hidden", Status: "hidden", Release: (bool?)false),
            (Slug: "preview-only", Email: "preview-owner@example.com", Title: "Matching preview", Status: "listed", Release: (bool?)true),
            (Slug: "unreleased", Email: "unreleased-owner@example.com", Title: "Matching unreleased", Status: "listed", Release: (bool?)null),
            (Slug: "now-preview", Email: "now-preview@example.com", Title: "Matching old stable", Status: "listed", Release: (bool?)false),
            (Slug: "now-stable", Email: "now-stable@example.com", Title: "Matching new stable", Status: "listed", Release: (bool?)true),
            (Slug: "unrelated", Email: "unrelated@example.com", Title: "Unrelated", Status: "listed", Release: (bool?)false)
        };
        foreach (var fixture in fixtures)
        {
            var owner = await tester.CreateConfirmedUser(fixture.Email);
            await AddPlugin(conn, fixture.Slug, owner, fixture.Title, fixture.Status, fixture.Release);
        }
        // Eligibility follows the latest release, including when a stable release was followed by a preview.
        await AddRelease(conn, "now-preview", 1, true);
        await AddRelease(conn, "now-stable", 1, false);

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await OpenRecipients(tester, "SearchText=MaTcHiNg&Status=Listed&Count=1&Sort=recent");
        await AssertRecipients(tester.Page!, "slug-owner@example.com", "title-owner@example.com",
            "matching-owner@example.com", "now-stable@example.com");

        await OpenRecipients(tester, "SearchText=MaTcHiNg&Status=Hidden&Count=1");
        await AssertRecipients(tester.Page!, "hidden-owner@example.com");

        foreach (var search in new[] { "preview-only", "unreleased", "no-such-plugin" })
        {
            await tester.GoToUrl($"/admin/plugins?SearchText={search}");
            await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Expect(OwnerEmailLink(tester.Page)).ToHaveCountAsync(0);
        }
    }

    [Fact]
    public async Task EmailPrimaryOwners_Deduplicates_Email_Addresses_Case_Insensitively()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        const string ownerEmail = "shared-owner@example.com";
        var firstOwner = await tester.CreateConfirmedUser(ownerEmail);
        var secondOwner = await tester.CreateConfirmedUser("second-account@example.com");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        // Preserve distinct account names while giving both records the same email with different casing.
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "Email" = @email, "NormalizedEmail" = @email WHERE "Id" = @secondOwner;
            """, new { email = ownerEmail.ToUpperInvariant(), secondOwner });
        await AddPlugin(conn, "email-first", firstOwner);
        await AddPlugin(conn, "email-second", firstOwner);
        await AddPlugin(conn, "email-third", secondOwner);

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await OpenRecipients(tester, "Count=1&Skip=2");
        await AssertRecipients(tester.Page!, ownerEmail);
    }

    [Fact]
    public async Task EmailPrimaryOwners_Does_Not_Put_Recipient_List_In_The_Url()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var emails = Enumerable.Range(0, 40)
            .Select(i => $"owner-{i:D2}-with-a-long-email-address-for-pagination@example.com").ToArray();
        Assert.True(string.Join(",", emails).Length > 2000);
        foreach (var (email, index) in emails.Select((email, index) => (email, index)))
        {
            var owner = await tester.CreateConfirmedUser(email);
            await AddPlugin(conn, $"email-{index:D2}", owner);
        }

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl("/admin/plugins?Count=100");
        var link = OwnerEmailLink(tester.Page!);
        var href = await link.GetAttributeAsync("href");
        Assert.NotNull(href);
        Assert.DoesNotContain("@", Uri.UnescapeDataString(href));
        await link.ClickAsync();
        await AssertRecipients(tester.Page!, emails);
        Assert.DoesNotContain("@", Uri.UnescapeDataString(tester.Page!.Url));
    }

    [Fact]
    public async Task EmailPrimaryOwners_With_No_Eligible_Matches_Returns_To_Filtered_Plugin_List()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        var owner = await tester.CreateConfirmedUser("preview-owner@example.com");
        const string search = "Preview & staging";
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await AddPlugin(conn, "preview-only", owner, title: search, preRelease: true);
        await AddPlugin(conn, "unrelated-stable", owner, title: "Unrelated stable plugin");

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl($"/admin/plugins/email-primary-owners?SearchText={Uri.EscapeDataString(search)}&Status=listed");
        var page = tester.Page!;
        await Expect(page.Locator(".alert-warning")).ToContainTextAsync(
            "No primary owners with a stable latest release match these filters.");
        Assert.Equal("/admin/plugins", new Uri(page.Url).AbsolutePath);
        await Expect(page.Locator("#SearchText")).ToHaveValueAsync(search);
        await Expect(page.Locator("#Status")).ToHaveValueAsync("listed");
        await Expect(page.Locator("#To")).ToHaveCountAsync(0);
    }

    private static ILocator OwnerEmailLink(IPage page) =>
        page.GetByRole(AriaRole.Link, new() { Name = "Email Primary Owners", Exact = true });

    private static async Task OpenRecipients(PlaywrightTester tester, string query)
    {
        await tester.GoToUrl($"/admin/plugins?{query}");
        await OwnerEmailLink(tester.Page!).ClickAsync();
    }

    private async Task AssertRecipients(IPage page, params string[] expected)
    {
        await Expect(page.Locator("#To")).ToBeVisibleAsync();
        var actual = (await page.Locator("#To").InputValueAsync())
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase),
            actual.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task AddPlugin(NpgsqlConnection conn, string slug, string owner,
        string title = "", string visibility = "listed", bool? preRelease = false)
    {
        Assert.True(await conn.NewPlugin(new PluginSlug(slug), owner));
        await conn.ExecuteAsync("""
            UPDATE plugins SET visibility = @visibility::plugin_visibility_enum,
                settings = jsonb_build_object('pluginTitle', @title::text) WHERE slug = @slug;
            """, new { slug, visibility, title });
        if (preRelease is not null)
            await AddRelease(conn, slug, 0, preRelease.Value);
    }

    private static Task AddRelease(NpgsqlConnection conn, string slug, int buildId, bool preRelease) =>
        conn.ExecuteAsync("""
            INSERT INTO builds(plugin_slug, id, state, build_info) VALUES (@slug, @buildId, 'uploaded', '{}');
            INSERT INTO versions(plugin_slug, ver, build_id, btcpay_min_ver, pre_release, updated_at)
            VALUES (@slug, ARRAY[@buildId + 1,0,0,0], @buildId, ARRAY[2,0,0,0], @preRelease, @updatedAt);
            """, new { slug, buildId, preRelease, updatedAt = DateTime.UtcNow.AddDays(-10 + buildId) });
}
