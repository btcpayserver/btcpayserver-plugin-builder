using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Newtonsoft.Json;
using Npgsql;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Services;

public class EmailService(DBConnectionFactory connectionFactory,
    AdminSettingsCache adminSettingsCache)
{
    public Task<List<string>> SendEmail(string toCsvList, string subject, string messageText)
    {
        List<InternetAddress> toList = toCsvList.Split([","], StringSplitOptions.RemoveEmptyEntries)
            .Select(InternetAddress.Parse)
            .ToList();
        return DeliverEmail(toList, subject, messageText);
    }

    protected virtual async Task<List<string>> DeliverEmail(IEnumerable<InternetAddress> toList, string subject, string messageText)
    {
        List<string> recipients = new();
        var emailSettings = await GetEmailSettingsFromDb();
        if (emailSettings == null)
            throw new InvalidOperationException("Email settings not configured. Please set up email settings in the admin panel.");

        var smtpClient = await CreateSmtpClient(emailSettings);
        MimeMessage message = new();
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

    public bool IsValidEmailList(string to) => to.Split(',').Select(email => email.Trim()).All(email => !string.IsNullOrWhiteSpace(email) && Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));

    public Task SendVerifyEmail(string toEmail, string verifyUrl)
    {
        var body = $"Please verify your account by visiting: {verifyUrl}";

        return SendEmail(toEmail, "Verify your account on BTCPay Server Plugin Builder", body);
    }

    public async Task NotifyAdminOnNewRequestListing(NpgsqlConnection conn, PluginSlug pluginSlug, string pluginPublicUrl, string listingPageUrl)
    {
        var notificationSettingEmails = await conn.GetFirstPluginBuildReviewersSetting();
        if (string.IsNullOrEmpty(notificationSettingEmails))
            return;

        var toList = notificationSettingEmails.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(MailboxAddressValidator.Parse);
        var body = $@"
Hello Admin,

A new plugin has just been published on the BTCPay Server Plugin Builder and is requesting lisitng to public page.

Plugin URL: {pluginPublicUrl}

Listing Page: {listingPageUrl}

Please review and list the plugin details as soon as possible.

Thank you,
BTCPay Server Plugin Builder";
        try
        {
            await DeliverEmail(toList, "New Plugin Request Listing on BTCPay Server Plugin Builder", body);
        }
        catch (Exception) { }
    }

    public async Task<EmailSettingsViewModel?> GetEmailSettingsFromDb()
    {
        await using var conn = await connectionFactory.Open();
        var jsonEmail = await conn.SettingsGetAsync("EmailSettings");
        var emailSettings = string.IsNullOrEmpty(jsonEmail)
            ? null
            : JsonConvert.DeserializeObject<EmailSettingsViewModel>(jsonEmail);
        return emailSettings;
    }

    public async Task SaveEmailSettingsToDatabase(EmailSettingsViewModel model)
    {
        await using var conn = await connectionFactory.Open();
        var emailSettingsJson = JsonConvert.SerializeObject(model);
        await conn.SettingsSetAsync("EmailSettings", emailSettingsJson);
        await adminSettingsCache.RefreshAllVerifiedEmailSettings(conn);
    }

    public async Task<SmtpClient> CreateSmtpClient(EmailSettingsViewModel settings)
    {
        SmtpClient client = new();
        using CancellationTokenSource connectCancel = new(10000);
        try
        {
            if (settings.DisableCertificateCheck)
            {
                client.CheckCertificateRevocation = false;
#pragma warning disable CA5359 // Do Not Disable Certificate Validation
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;
#pragma warning restore CA5359 // Do Not Disable Certificate Validation
            }

            await client.ConnectAsync(settings.Server, settings.Port, SecureSocketOptions.Auto,
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

    public Task SendPasswordResetLinkAsync(string toEmail, string passwordResetUrl)
    {
        var body = $"Please reset your password by visiting following link: {passwordResetUrl}";

        return SendEmail(toEmail, "Reset your password on BTCPay Server Plugin Builder", body);
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
    private static readonly ParserOptions _options;

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
        if (string.IsNullOrWhiteSpace(str)) return false;
        return MailboxAddress.TryParse(_options, str, out mailboxAddress) && mailboxAddress is not null;
    }
}
