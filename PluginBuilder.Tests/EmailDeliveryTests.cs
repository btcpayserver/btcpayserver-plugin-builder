using MailKit.Net.Smtp;
using PluginBuilder.Services;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class EmailDeliveryTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string First = "\"First, Owner\" <first@example.com>";
    private const string Second = "second@example.com";
    private const string Third = "third@example.com";
    private const string Recipients = First + "," + Second + "," + Third;

    [Fact]
    public async Task RejectedRecipientPreservesAcceptedAndUnsentRecipients()
    {
        await using var smtp = new ScriptedSmtpServer { RejectRecipient = Second };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);

        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            emails.SendEmail(Recipients, "Partial delivery", "Test message"));

        Assert.Equal(new[] { First }, error.AcceptedRecipients);
        Assert.Equal(new[] { Second, Third }, error.UnsentRecipients);
        Assert.Null(error.UncertainRecipient);
        Assert.Equal(SmtpErrorCode.RecipientNotAccepted,
            Assert.IsType<SmtpCommandException>(error.InnerException).ErrorCode);
        Assert.Equal(new[] { "first@example.com", Second }, smtp.AttemptedRecipients);
        Assert.Equal(new[] { "first@example.com" }, smtp.AcceptedRecipients);
    }

    [Fact]
    public async Task LostDataResponseLeavesCurrentRecipientUncertain()
    {
        await using var smtp = new ScriptedSmtpServer { DisconnectAfterDataRecipient = Second };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);

        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            emails.SendEmail(Recipients, "Lost SMTP response", "Test message"));

        Assert.Equal(new[] { First }, error.AcceptedRecipients);
        Assert.Equal(new[] { Third }, error.UnsentRecipients);
        Assert.Equal(Second, error.UncertainRecipient);
        Assert.Equal(new[] { "first@example.com", Second }, smtp.AttemptedRecipients);
        // The server took the second message, but the client never received its acknowledgement.
        Assert.Equal(new[] { "first@example.com", Second }, smtp.AcceptedRecipients);
    }

    [Fact]
    public async Task EachRecipientGetsItsOwnSendTimeout()
    {
        await using var smtp = new ScriptedSmtpServer { DeliveryDelay = TimeSpan.FromSeconds(16) };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);

        // Each send fits within 30 seconds; together they exceed the old batch-wide timeout.
        var accepted = await emails.SendEmail(First + "," + Second, "Slow batch", "Test message")
            .WaitAsync(TimeSpan.FromSeconds(45));

        Assert.Equal(new[] { First, Second }, accepted);
        Assert.Equal(new[] { "first@example.com", Second }, smtp.AcceptedRecipients);
    }

    [Fact]
    public async Task StalledSendTimesOutWithoutRetryingUncertainRecipient()
    {
        await using var smtp = new ScriptedSmtpServer { DeliveryDelay = TimeSpan.FromMinutes(1) };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);

        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            emails.SendEmail(Recipients, "Stalled SMTP response", "Test message")
                .WaitAsync(TimeSpan.FromSeconds(45)));

        Assert.Empty(error.AcceptedRecipients);
        Assert.Equal(new[] { Second, Third }, error.UnsentRecipients);
        Assert.Equal(First, error.UncertainRecipient);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(new[] { "first@example.com" }, smtp.AttemptedRecipients);
        Assert.Empty(smtp.AcceptedRecipients);
    }

    [Fact]
    public async Task CallerCancellationRemainsOperationCanceledException()
    {
        await using var smtp = new ScriptedSmtpServer { DeliveryDelay = TimeSpan.FromMinutes(1) };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);
        using var cancellation = new CancellationTokenSource();

        var sending = emails.SendEmail(Recipients, "Canceled send", "Test message", cancellation.Token);
        await smtp.FirstMessageReceived.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "first@example.com" }, smtp.AttemptedRecipients);
        Assert.Empty(smtp.AcceptedRecipients);
    }

    [Fact]
    public async Task QuitFailureDoesNotDiscardAcceptedRecipients()
    {
        await using var smtp = new ScriptedSmtpServer { RejectQuit = true };
        await using var tester = Create();
        var emails = await ConfigureEmailService(tester, smtp);

        var accepted = await emails.SendEmail(Recipients, "Rejected quit", "Test message");

        Assert.Equal(new[] { First, Second, Third }, accepted);
        Assert.Equal(new[] { "first@example.com", Second, Third }, smtp.AcceptedRecipients);
    }

    private static async Task<EmailService> ConfigureEmailService(ServerTester tester, ScriptedSmtpServer smtp)
    {
        tester.ReuseDatabase = false;
        await tester.Start();
        var emails = tester.GetService<EmailService>();
        await emails.SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = "127.0.0.1",
            Port = smtp.Port,
            From = "sender@example.com"
        });
        return emails;
    }
}
