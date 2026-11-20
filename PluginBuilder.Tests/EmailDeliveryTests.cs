using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using PluginBuilder.Controllers.Logic;
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
    private static readonly string[] _recipientAddresses = ["first@example.com", Second, Third];

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

    [Theory]
    [InlineData(0, "none", 1)]
    [InlineData(1, "1-1", 2)]
    [InlineData(2, "1-2", 3)]
    public async Task CancellationBetweenRecipientsLogsAcceptedAndNotAttemptedPositions(
        int acceptedCount, string acceptedPositions, int currentPosition)
    {
        await using var smtp = new ScriptedSmtpServer();
        await using var tester = Create();
        await ConfigureEmailService(tester, smtp);
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingEmailLogger();
        var emails = new CancelAfterRecipientsEmailService(
            tester.GetService<DBConnectionFactory>(), tester.GetService<AdminSettingsCache>(), logger, cancellation,
            acceptedCount);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            emails.SendEmail(First + ", \"Second, Owner\" <second@example.com>, " + Third,
                    "Sensitive subject", "Sensitive message body", cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(_recipientAddresses.Take(acceptedCount), smtp.AttemptedRecipients);
        Assert.Equal(_recipientAddresses.Take(acceptedCount), smtp.AcceptedRecipients);
        AssertCancellationLog(logger, acceptedCount, acceptedPositions, currentPosition, "not_attempted");
    }

    [Theory]
    [InlineData(0, "none", 1)]
    [InlineData(1, "1-1", 2)]
    [InlineData(2, "1-2", 3)]
    public async Task CancellationDuringSendLogsAcceptedAndUnconfirmedPositions(
        int acceptedCount, string acceptedPositions, int currentPosition)
    {
        using var cancellation = new CancellationTokenSource();
        await using var smtp = new ScriptedSmtpServer
        {
            BeforeMessageAccepted = async (recipient, stop) =>
            {
                if (recipient == _recipientAddresses[acceptedCount])
                {
                    cancellation.Cancel();
                    // Do not acknowledge the current message while the client handles cancellation.
                    await Task.Delay(Timeout.Infinite, stop);
                }
            }
        };
        await using var tester = Create();
        await ConfigureEmailService(tester, smtp);
        var logger = new RecordingEmailLogger();
        var emails = new EmailService(
            tester.GetService<DBConnectionFactory>(), tester.GetService<AdminSettingsCache>(), logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            emails.SendEmail(First + ", \"Second, Owner\" <second@example.com>, " + Third,
                    "Sensitive subject", "Sensitive message body", cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(_recipientAddresses.Take(currentPosition), smtp.AttemptedRecipients);
        Assert.Equal(_recipientAddresses.Take(acceptedCount), smtp.AcceptedRecipients);
        AssertCancellationLog(logger, acceptedCount, acceptedPositions, currentPosition, "unconfirmed");
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

    private static void AssertCancellationLog(RecordingEmailLogger logger, int acceptedCount,
        string acceptedPositions, int currentPosition, string status)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain("@", entry.Message);
        Assert.All(entry.Values.Values, value => Assert.DoesNotContain("@", value?.ToString() ?? ""));
        Assert.Equal(acceptedCount, entry.Values["AcceptedCount"]);
        Assert.Equal(acceptedPositions, entry.Values["AcceptedPositions"]);
        Assert.Equal(currentPosition, entry.Values["CurrentPosition"]);
        Assert.Equal(status, entry.Values["CurrentStatus"]);
        Assert.DoesNotContain("First, Owner", entry.Message);
        Assert.DoesNotContain("Second, Owner", entry.Message);
        Assert.DoesNotContain("Sensitive subject", entry.Message);
        Assert.DoesNotContain("Sensitive message body", entry.Message);
    }

    private sealed class CancelAfterRecipientsEmailService(
        DBConnectionFactory connectionFactory,
        AdminSettingsCache adminSettingsCache,
        ILogger<EmailService> logger,
        CancellationTokenSource cancellation,
        int acceptedCount)
        : EmailService(connectionFactory, adminSettingsCache, logger)
    {
        public override async Task<SmtpClient> CreateSmtpClient(EmailSettingsViewModel settings,
            CancellationToken cancellationToken = default)
        {
            var client = await base.CreateSmtpClient(settings, cancellationToken);
            if (acceptedCount == 0)
                cancellation.Cancel();
            var sentCount = 0;
            // MailKit raises this after the server confirms acceptance, before SendAsync returns.
            client.MessageSent += (_, _) =>
            {
                if (++sentCount == acceptedCount)
                    cancellation.Cancel();
            };
            return client;
        }
    }

    private sealed class RecordingEmailLogger : ILogger<EmailService>
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Entries.Add((logLevel, formatter(state, exception), values));
        }
    }
}
