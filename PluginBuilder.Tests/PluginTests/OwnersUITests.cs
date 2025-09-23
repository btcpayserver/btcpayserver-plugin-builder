using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class OwnersUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("OwnersUITest", output);

    [Fact]
    public async Task Ownership_Flow_Works()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();

        await t.GoToUrl("/register");
        var userA = await t.RegisterNewUser();
        await Expect(t.Page!.Locator("body")).ToContainTextAsync("Builds");

        var slug = "owners-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page!.FillAsync("#PluginSlug", slug);
        await t.Page.ClickAsync("#Create");
        await Expect(t.Page.Locator(".alert-warning")).ToBeVisibleAsync();

        await t.VerifyEmailAndGithubAsync(userA);
        await t.GoToUrl("/plugins/create");
        await t.Page!.FillAsync("#PluginSlug", slug);
        await t.Page.ClickAsync("#Create");
        await t.GoToUrl($"/plugins/{slug}/owners");
        await t.AssertNoError();
        await Expect(t.Page.Locator(".list-group-item .fw-semibold")).ToContainTextAsync(userA);
        await Expect(t.Page.Locator(".list-group-item")).ToContainTextAsync("Primary Owner");

        await t.Logout();
        await t.GoToUrl("/register");
        var userB = await t.RegisterNewUser();
        await Expect(t.Page.Locator("body")).ToContainTextAsync("Builds");
        await t.Logout();

        await t.GoToLogin();
        await t.LogIn(userA);
        await t.GoToUrl($"/plugins/{slug}/owners");

        var addForm = t.Page.Locator("form[method='post'] >> input[name='email']");

        await addForm.FillAsync(userB);
        await t.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add" }).ClickAsync();
        await Expect(t.Page.Locator(".alert-warning")).ToBeVisibleAsync();

        await t.VerifyEmailAndGithubAsync(userB);
        await addForm.FillAsync(userB);
        await t.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add" }).ClickAsync();

        var bRow = t.Page.Locator(".list-group-item").Filter(new LocatorFilterOptions { HasText = userB });
        await Expect(bRow).ToBeVisibleAsync();

        var removeBtn = bRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove" });
        await removeBtn.ClickAsync();
        var confirmBtn = t.Page.Locator("#ConfirmContinue");
        await Expect(confirmBtn).ToBeVisibleAsync();
        await confirmBtn.ClickAsync();
        await Expect(t.Page.Locator(".list-group-item").Filter(new LocatorFilterOptions { HasText = userB })).ToHaveCountAsync(0);

        await addForm.FillAsync(userB);
        await t.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add" }).ClickAsync();
        bRow = t.Page.Locator(".list-group-item").Filter(new LocatorFilterOptions { HasText = userB });
        await Expect(bRow).ToBeVisibleAsync();
        var transferBtn = bRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Transfer Primary" });
        await transferBtn.ClickAsync();
        await Expect(t.Page.Locator("#ConfirmContinue")).ToBeVisibleAsync();
        await t.Page.ClickAsync("#ConfirmContinue");
        await Expect(bRow).ToContainTextAsync("Primary Owner");
        await Expect(t.Page.Locator("form[method='post'] >> input[name='email']")).ToHaveCountAsync(0);

        var aRow = t.Page.Locator(".list-group-item").Filter(new LocatorFilterOptions { HasText = userA });
        var leaveBtn = aRow.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Leave" });
        await leaveBtn.ClickAsync();
        await Task.WhenAll(
            t.Page.WaitForURLAsync(url => !url.EndsWith($"/plugins/{slug}/owners")),
            t.Page.ClickAsync("#ConfirmContinue")
        );

        await Expect(t.Page.Locator(".alert-success"))
            .ToContainTextAsync(new Regex("(Owner removed|You have left)", RegexOptions.IgnoreCase));

        await t.Logout();
        await t.GoToLogin();
        await t.LogIn(userB);

        await t.GoToUrl($"/plugins/{slug}");
        await t.AssertNoError();
        await Expect(t.Page).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(slug)}"));

        var createNewBuildLink = t.Page.Locator("#CreateNewBuild");
        await Expect(createNewBuildLink).ToBeVisibleAsync();
        await createNewBuildLink.ClickAsync();

        await Expect(t.Page).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(slug)}/create$"));

        await Expect(t.Page.Locator("#GitRepository")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#GitRef")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#PluginDirectory")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#BuildConfig")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#Create")).ToBeVisibleAsync();

        await t.AssertNoError();
    }
}
