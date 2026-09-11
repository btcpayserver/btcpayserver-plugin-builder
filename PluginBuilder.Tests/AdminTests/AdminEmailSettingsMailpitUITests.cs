using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminEmailSettingsMailpitUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminEmailSettingsMailpitUITests", output);

    [Fact]
    public async Task EmailPrimaryOwners_Sends_Once_Per_Owner_With_Multiple_Plugins()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();
        const string ownerEmail = "multiple-plugins@example.com";
        const string otherEmail = "other-owner@example.com";
        var ownerId = await tester.CreateConfirmedUser(ownerEmail);
        var otherId = await tester.CreateConfirmedUser(otherEmail);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        foreach (var (slug, userId) in new[] { ("email-first", ownerId), ("email-second", ownerId), ("email-third", otherId) })
        {
            Assert.True(await conn.NewPlugin(new PluginSlug(slug), userId));
            // Only released-plugin metadata is needed for the recipients CTA; no build is queued.
            await conn.ExecuteAsync("""
                INSERT INTO builds(plugin_slug, id, state, build_info) VALUES (@slug, 0, 'uploaded', '{}');
                INSERT INTO versions(plugin_slug, ver, build_id, btcpay_min_ver, pre_release)
                VALUES (@slug, ARRAY[1,0,0,0], 0, ARRAY[2,0,0,0], false);
                """, new { slug });
        }

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl("/admin/plugins");
        var page = tester.Page!;
        await page.GetByRole(AriaRole.Link, new() { Name = "Email Primary Owners", Exact = true }).ClickAsync();
        await Expect(page.Locator("#To")).ToHaveValueAsync($"{ownerEmail},{otherEmail}");
        var subject = $"Unique plugin owners {Guid.NewGuid():N}";
        await page.FillAsync("#Subject", subject);
        await page.FillAsync("#Message", "One notification per plugin owner.");
        var first = await tester.Server.AssertHasEmail(subject, ownerEmail, () =>
            page.GetByRole(AriaRole.Button, new() { Name = "Send Email", Exact = true }).ClickAsync());
        var second = await tester.Server.AssertHasEmail(subject, otherEmail, () => Task.CompletedTask);

        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Emails sent successfully to 2 recipient(s).");
        Assert.Equal(ownerEmail, Assert.Single(first.To).Address);
        Assert.Equal(otherEmail, Assert.Single(second.To).Address);
        using var mailpit = tester.Server.GetMailPitClient();
        Assert.Equal(2, (await mailpit.Search($"subject:\"{subject}\"")).Messages.Count);
    }

    [Fact]
    public async Task EmailSender_Accepts_Quoted_Display_Names_And_Rejects_Invalid_Recipients()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.ConfigureMailpitSmtp();

        var adminEmail = await tester.CreateServerAdminAsync();
        await tester.LogIn(adminEmail);
        const string recipients = "\"Doe, John\" <owner@example.com>, second@example.com";
        await tester.GoToUrl($"/admin/emailsender?to={Uri.EscapeDataString(recipients)}");
        var page = tester.Page!;
        await Expect(page.Locator("#To")).ToHaveValueAsync(recipients);
        var subject = $"Quoted recipient test {Guid.NewGuid():N}";
        await page.FillAsync("#Subject", subject);
        await page.FillAsync("#Message", "Test message with a quoted recipient name");

        var first = await tester.Server.AssertHasEmail(subject, "owner@example.com", () =>
            page.GetByRole(AriaRole.Button, new() { Name = "Send Email", Exact = true }).ClickAsync());
        var second = await tester.Server.AssertHasEmail(subject, "second@example.com", () => Task.CompletedTask);

        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Emails sent successfully to 2 recipient(s).");
        Assert.Equal("owner@example.com", Assert.Single(first.To).Address);
        Assert.Equal("Doe, John", first.To[0].Name);
        Assert.Equal("second@example.com", Assert.Single(second.To).Address);
        Assert.Contains("Test message with a quoted recipient name", first.Text);

        await page.FillAsync("#To", "owner@localhost");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send Email", Exact = true }).ClickAsync();

        await Expect(page.Locator("[data-valmsg-for='To']")).ToContainTextAsync("Invalid email format");
        await Expect(page.Locator("#To")).ToHaveValueAsync("owner@localhost");
        await Expect(page.Locator(".alert-success")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CheatMode_UseMailpitButton_Saves_Local_Smtp_Settings()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        tester.Server.CheatMode = true;
        await tester.StartAsync();

        var adminEmail = await tester.CreateServerAdminAsync();
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/emailsettings");
        // GoToUrl returns on Commit; wait for the DOM before asserting on rendered elements.
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var mailpitButton = tester.Page.Locator("#mailpit");
        await Expect(mailpitButton).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await mailpitButton.ClickAsync();

        await Expect(tester.Page).ToHaveURLAsync(new Regex("/admin/emailsettings$"));
        await Expect(tester.Page.Locator(".alert-success")).ToContainTextAsync("Mailpit SMTP settings saved");

        // The button persists the local Mailpit SMTP settings to the database.
        var saved = await tester.Server.GetService<EmailService>().GetEmailSettingsFromDb();
        Assert.NotNull(saved);
        Assert.Equal(MailpitDevSettings.Host, saved!.Server);
        Assert.Equal(MailpitDevSettings.SmtpPort, saved.Port);
        Assert.True(saved.DisableCertificateCheck);
    }

    [Fact]
    public async Task Save_Button_Persists_Entered_Smtp_Settings()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var adminEmail = await tester.CreateServerAdminAsync();
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/emailsettings");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Point at the real Mailpit instance so ValidateSmtpConnection (a live SMTP connect) succeeds.
        await tester.Page.FillAsync("#Server", MailpitDevSettings.Host);
        await tester.Page.FillAsync("#Port", MailpitDevSettings.SmtpPort.ToString());
        await tester.Page.FillAsync("#Username", "admin@example.com");
        await tester.Page.FillAsync("#Password", "password");
        await tester.Page.FillAsync("#From", "admin@example.com");
        await tester.Page.CheckAsync("#DisableCertificateCheck");
        await tester.Page.ClickAsync("#Save");

        // The (now explicit asp-action="EmailSettings") form must post, validate, save, and redirect.
        await Expect(tester.Page).ToHaveURLAsync(new Regex("/admin/emailsettings$"));
        await Expect(tester.Page.Locator(".alert-success")).ToContainTextAsync("SMTP settings updated");

        var saved = await tester.Server.GetService<EmailService>().GetEmailSettingsFromDb();
        Assert.NotNull(saved);
        Assert.Equal(MailpitDevSettings.Host, saved!.Server);
        Assert.Equal(MailpitDevSettings.SmtpPort, saved.Port);
        Assert.Equal("admin@example.com", saved.From);
    }

    [Fact]
    public async Task Without_CheatMode_UseMailpitButton_Is_Not_Rendered()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        // CheatMode defaults to false.
        await tester.StartAsync();

        var adminEmail = await tester.CreateServerAdminAsync();
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/emailsettings");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The Save button is always present; the Use mailpit button only shows in cheat mode.
        // Wait for Save first so the page is fully rendered before asserting the button's absence.
        await Expect(tester.Page.Locator("#Save")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(tester.Page.Locator("#mailpit")).ToHaveCountAsync(0);
    }
}
