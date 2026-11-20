using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminEmailPartialDeliveryUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminEmailPartialDeliveryUITests", output);

    [Fact]
    public async Task EmailSender_Excludes_Accepted_Recipients_After_Partial_Failure()
    {
        await using var smtp = new ScriptedSmtpServer { RejectRecipient = "second@example.com" };
        await using var tester = new PlaywrightTester(_log);
        const string recipients = "first@example.com, second@example.com, third@example.com";
        await OpenComposer(tester, smtp, recipients);
        var page = tester.Page!;
        await Submit(page);
        await Expect(page.Locator(".alert-warning")).ToBeVisibleAsync();

        output.WriteLine($"SMTP accepted: {string.Join(", ", smtp.AcceptedRecipients)}");
        output.WriteLine($"SMTP attempted: {string.Join(", ", smtp.AttemptedRecipients)}");
        output.WriteLine($"Warning: {await page.Locator(".alert-warning").InnerTextAsync()}");
        output.WriteLine($"Retry recipients: {await page.Locator("#To").InputValueAsync()}");
        Assert.Equal(["first@example.com"], smtp.AcceptedRecipients.ToArray());
        Assert.Equal(["first@example.com", "second@example.com"], smtp.AttemptedRecipients.ToArray());
        await Expect(page.Locator("#To")).ToHaveValueAsync("second@example.com, third@example.com");
        await Expect(page.Locator("#accepted-recipients")).ToContainTextAsync("first@example.com");
        await Expect(page.Locator("#Subject")).ToHaveValueAsync("Partial delivery reproduction");

        smtp.RejectRecipient = null;
        await Submit(page);
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Emails sent successfully to 2 recipient(s).");
        Assert.Equal(["first@example.com", "second@example.com", "third@example.com"], smtp.AcceptedRecipients.ToArray());
        Assert.Equal(["first@example.com", "second@example.com", "second@example.com", "third@example.com"],
            smtp.AttemptedRecipients.ToArray());
    }

    [Fact]
    public async Task EmailSender_Does_Not_Retry_A_Recipient_With_An_Unknown_Delivery_Result()
    {
        await using var smtp = new ScriptedSmtpServer { DisconnectAfterDataRecipient = "second@example.com" };
        await using var tester = new PlaywrightTester(_log);
        const string recipients = "first@example.com, \"Doe, Jane\" <second@example.com>, \"Doe, John\" <third@example.com>";
        await OpenComposer(tester, smtp, recipients);
        var page = tester.Page!;
        await Submit(page);

        await Expect(page.Locator("#To")).ToHaveValueAsync("\"Doe, John\" <third@example.com>");
        await Expect(page.Locator("#accepted-recipients")).ToContainTextAsync("first@example.com");
        await Expect(page.Locator("#uncertain-recipient")).ToContainTextAsync("second@example.com");
        await Expect(page.Locator("#uncertain-recipient")).ToContainTextAsync("Doe, Jane");
        await Expect(page.Locator("#uncertain-recipient")).ToContainTextAsync("Check the mail server's logs before sending to this address again.");
        Assert.Equal(["first@example.com", "second@example.com"], smtp.AcceptedRecipients.ToArray());
        Assert.Equal(["first@example.com", "second@example.com"], smtp.AttemptedRecipients.ToArray());

        await Submit(page);
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Emails sent successfully to 1 recipient(s).");
        Assert.Equal(["first@example.com", "second@example.com", "third@example.com"], smtp.AcceptedRecipients.ToArray());
        Assert.Equal(["first@example.com", "second@example.com", "third@example.com"], smtp.AttemptedRecipients.ToArray());
    }

    [Fact]
    public async Task EmailSender_Shows_An_Uncertain_Result_When_No_Retry_Recipients_Remain()
    {
        const string recipient = "only@example.com";
        await using var smtp = new ScriptedSmtpServer { DisconnectAfterDataRecipient = recipient };
        await using var tester = new PlaywrightTester(_log);
        await OpenComposer(tester, smtp, recipient);
        var page = tester.Page!;
        await Submit(page);

        await Expect(page.Locator("#To")).ToHaveValueAsync("");
        await Expect(page.Locator("#accepted-recipients")).ToHaveCountAsync(0);
        await Expect(page.Locator("#uncertain-recipient")).ToContainTextAsync(recipient);
        await Expect(page.Locator("#uncertain-recipient")).ToContainTextAsync("Check the mail server's logs before sending to this address again.");
        await Expect(page.Locator("#Subject")).ToHaveValueAsync("Partial delivery reproduction");
        Assert.Equal([recipient], smtp.AttemptedRecipients.ToArray());
        Assert.Equal([recipient], smtp.AcceptedRecipients.ToArray());
    }

    private static async Task OpenComposer(PlaywrightTester tester, ScriptedSmtpServer smtp, string recipients)
    {
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await tester.Server.GetService<EmailService>().SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = "127.0.0.1",
            Port = smtp.Port,
            From = "sender@example.com",
            Username = "test",
            Password = "test"
        });
        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl($"/admin/emailsender?to={Uri.EscapeDataString(recipients)}");
        await tester.Page!.FillAsync("#Subject", "Partial delivery reproduction");
        await tester.Page.FillAsync("#Message", "A local SMTP failure should preserve completed deliveries.");
    }

    private async Task Submit(IPage page)
    {
        await Expect(page.Locator("form").Filter(new() { Has = page.Locator("#To") })
            .Locator("input[name='__RequestVerificationToken']")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Send Email", Exact = true }).ClickAsync();
    }
}
