using System.Diagnostics.CodeAnalysis;
using MailKit.Net.Smtp;
using MimeKit;
using Newtonsoft.Json;
using Npgsql;
using PluginBuilder.Extensions;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Services;

public class EmailService
{
    private readonly DBConnectionFactory _connectionFactory;

    public EmailService(DBConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<List<string>> SendEmail(string toCsvList, string subject, string messageText)
    {
        var toList = toCsvList.Split([","], StringSplitOptions.RemoveEmptyEntries)
            .Select(InternetAddress.Parse)
            .ToList();
        return SendEmail(toList, subject, messageText);
    }
    
    private async Task<List<string>> SendEmail(IEnumerable<InternetAddress> toList, string subject, string messageText)    
    {
        List<string> recipients = new List<string>();
        var emailSettings = await GetEmailSettingsFromDb();
        var smtpClient = await CreateSmtpClient(emailSettings);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(emailSettings.From));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = messageText };
        foreach (var email in toList)
        {
            message.To.Clear();
            message.To.Add(email);
            await smtpClient.SendAsync(message);
            recipients.Add(email.ToString());
        }
        await smtpClient.DisconnectAsync(true);
        return recipients;
    }
    
    public Task SendVerifyEmail(string toEmail, string verifyUrl)
    {
        var body = $"Please verify your account by visiting: {verifyUrl}";

        return SendEmail(toEmail, "Verify your account on BTCPay Server Plugin Builder", body);
    }

    public async Task<EmailSettingsViewModel?> GetEmailSettingsFromDb()
    {
        await using var conn = await _connectionFactory.Open();
        var jsonEmail = await conn.SettingsGetAsync("EmailSettings");
        var emailSettings = string.IsNullOrEmpty(jsonEmail)
            ? null
            : JsonConvert.DeserializeObject<EmailSettingsViewModel>(jsonEmail);
        return emailSettings;
    }

    public async Task<SmtpClient> CreateSmtpClient(EmailSettingsViewModel settings)
    {
        SmtpClient client = new();
        using var connectCancel = new CancellationTokenSource(10000);
        try
        {
            if (settings.DisableCertificateCheck)
            {
                client.CheckCertificateRevocation = false;
#pragma warning disable CA5359 // Do Not Disable Certificate Validation
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;
#pragma warning restore CA5359 // Do Not Disable Certificate Validation
            }

            await client.ConnectAsync(settings.Server, settings.Port, MailKit.Security.SecureSocketOptions.Auto,
                connectCancel.Token);
            if ((client.Capabilities & SmtpCapabilities.Authentication) != 0)
                await client.AuthenticateAsync(settings.Username ?? string.Empty, settings.Password ?? string.Empty,
                    connectCancel.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }
}


// public MimeMessage CreateMailMessage(MailboxAddress to, string subject, string message, bool isHtml) =>
//     CreateMailMessage(new[] { to }, null, null, subject, message, isHtml);
// public MimeMessage CreateMailMessage(MailboxAddress[] to, MailboxAddress[] cc, MailboxAddress[] bcc, string subject, string message, bool isHtml)
// {
//     var bodyBuilder = new BodyBuilder();
//     if (isHtml)
//     {
//         bodyBuilder.HtmlBody = message;
//     }
//     else
//     {
//         bodyBuilder.TextBody = message;
//     }
//
//     var mm = new MimeMessage();
//     mm.Body = bodyBuilder.ToMessageBody();
//     mm.Subject = subject;
//     mm.From.Add(MailboxAddressValidator.Parse(Settings.From));
//     mm.To.AddRange(to);
//     mm.Cc.AddRange(cc ?? System.Array.Empty<InternetAddress>());
//     mm.Bcc.AddRange(bcc ?? System.Array.Empty<InternetAddress>());
//     return mm;
// }

public static class MailboxAddressValidator
{
    static ParserOptions _options;

    static MailboxAddressValidator()
    {
        _options = ParserOptions.Default.Clone();
        _options.AllowAddressesWithoutDomain = false;
    }

    public static bool IsMailboxAddress(string? str)
    {
        return TryParse(str, out _);
    }

    public static MailboxAddress Parse(string? str)
    {
        if (!TryParse(str, out var mb)) throw new FormatException("Invalid mailbox address (rfc822)");
        return mb;
    }

    public static bool TryParse(string? str, [MaybeNullWhen(false)] out MailboxAddress mailboxAddress)
    {
        mailboxAddress = null;
        if (String.IsNullOrWhiteSpace(str)) return false;
        return MailboxAddress.TryParse(_options, str, out mailboxAddress) && mailboxAddress is not null;
    }
}
