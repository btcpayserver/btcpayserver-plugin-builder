using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminEmailSettingsMailpitUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminEmailSettingsMailpitUITests", output);

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
