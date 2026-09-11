using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class EmailServiceTests
{
    [Theory]
    [InlineData("owner@example.com", new[] { "owner@example.com" })]
    [InlineData(" owner@example.com, second@example.com ", new[] { "owner@example.com", "second@example.com" })]
    [InlineData("\"Doe, John\" <owner@example.com>", new[] { "owner@example.com" })]
    [InlineData("\"Doe, John\" <owner@example.com>, second@example.com", new[] { "owner@example.com", "second@example.com" })]
    [InlineData("\"a,b\"@example.com", new[] { "\"a,b\"@example.com" })]
    [InlineData("owner@example.com,,second@example.com,", new[] { "owner@example.com", "second@example.com" })]
    public async Task ValidRecipientListsPreserveMailboxBoundaries(string input, string[] expectedRecipients)
    {
        var emails = new RecordingEmailService();

        Assert.True(emails.IsValidEmailList(input));
        await emails.SendEmail(input, "Test email", "Test message");

        Assert.Equal(expectedRecipients, emails.Recipients.Select(address => Assert.IsType<MailboxAddress>(address).Address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(",,")]
    [InlineData("invalid")]
    [InlineData("owner@localhost")]
    [InlineData("owner@")]
    [InlineData("owner@example.com, invalid")]
    [InlineData("\"Doe, John\" <owner@localhost>")]
    [InlineData("\"Doe, John <owner@example.com>")]
    [InlineData("Team:owner@example.com;")]
    [InlineData("Team:;")]
    public void InvalidRecipientListsAreRejected(string? input)
    {
        var emails = new RecordingEmailService();

        Assert.False(emails.IsValidEmailList(input));
    }

    private sealed class RecordingEmailService() : EmailService(null!, null!, NullLogger<EmailService>.Instance)
    {
        public InternetAddress[] Recipients { get; private set; } = [];

        protected override Task<List<string>> DeliverEmail(IEnumerable<InternetAddress> toList, string subject, string messageText,
            CancellationToken cancellationToken = default)
        {
            Recipients = toList.ToArray();
            return Task.FromResult(Recipients.Select(address => address.ToString()).ToList());
        }
    }
}
