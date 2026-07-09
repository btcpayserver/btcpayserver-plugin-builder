using PluginBuilder.Services;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class EmailTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    /// <summary>
    /// Smoke test for the Mailpit test harness: configure the app's SMTP settings to point at the
    /// local Mailpit instance (from PluginBuilder.Tests/docker-compose.yml), send an email, and assert
    /// it was captured by Mailpit.
    /// </summary>
    [Fact]
    public async Task CanSendEmailToMailpit()
    {
        await using var tester = await Start();

        var emailService = tester.GetService<EmailService>();
        await emailService.SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = ServerTester.MailPitHost,
            Port = ServerTester.MailPitSmtpPort,
            From = "test@example.com",
            Username = "test@example.com",
            Password = "test@example.com",
            DisableCertificateCheck = true
        });

        const string subject = "Plugin Builder Mailpit smoke test";
        const string body = "hello from the plugin builder test suite";

        var message = await tester.AssertHasEmail(subject, () =>
            emailService.SendEmail("destination@example.com", subject, body));

        Assert.Equal(subject, message.Subject);
        Assert.Contains(body, message.Text);
        Assert.Contains(message.To, t => t.Address == "destination@example.com");
    }
}
