using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminPluginDeleteUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminPluginDeleteUITests", output);

    [Fact]
    public async Task Admin_Can_Delete_Plugin_From_List()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var adminEmail = await CreateServerAdminAsync(tester);
        var ownerId = await tester.Server.CreateFakeUserAsync(email: "owner@admin-delete.test", confirmEmail: true, githubVerified: true);
        var slug = $"admin-delete-{Guid.NewGuid():N}".Substring(0, 20);
        await tester.Server.CreateAndBuildPluginAsync(ownerId, slug: slug);

        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/plugins");

        var row = tester.Page!.Locator("tbody tr", new PageLocatorOptions { HasTextString = slug }).First;
        await Expect(row).ToBeVisibleAsync();
        
        // Test Edit link navigation
        await row.Locator("a:has-text(\"Edit\")").ClickAsync();
        await Expect(tester.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/admin/plugins/edit/{slug}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        
        // Verify edit page displays plugin slug and identifier
        var pageHeading = tester.Page.Locator("h2 span.text-dark");
        await Expect(pageHeading).ToHaveTextAsync(slug);
        var identifierCode = tester.Page.Locator("code");
        await Expect(identifierCode.First).ToBeVisibleAsync();
        
        // Navigate back to plugins list
        await tester.GoToUrl("/admin/plugins");
        
        // Now test deletion
        var deleteRow = tester.Page.Locator("tbody tr", new PageLocatorOptions { HasTextString = slug }).First;
        await Expect(deleteRow).ToBeVisibleAsync();
        await deleteRow.Locator("a:has-text(\"Delete\")").ClickAsync();

        await Expect(tester.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/admin/plugins/delete/{slug}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var slugInput = tester.Page.Locator("input[name='PluginSlug']");
        await Expect(slugInput).ToHaveValueAsync(slug);

        await tester.Page.ClickAsync("#Delete");
        await Expect(tester.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/plugins", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        var deletedRow = tester.Page.Locator("tbody tr", new PageLocatorOptions { HasTextString = slug });
        await Expect(deletedRow).ToHaveCountAsync(0);

        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var remaining = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plugins WHERE slug=@slug", new { slug });
        Assert.Equal(0, remaining);
    }

    private static async Task<string> CreateServerAdminAsync(PlaywrightTester tester)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roleManager.RoleExistsAsync(Roles.ServerAdmin))
        {
            await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));
        }

        var email = $"admin-{Guid.NewGuid():N}@test.com";
        const string password = "123456";
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create admin user: {errors}");
        }

        await userManager.AddToRoleAsync(user, Roles.ServerAdmin);
        return email;
    }
}
