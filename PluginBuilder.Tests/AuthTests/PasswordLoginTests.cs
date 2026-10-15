using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PasswordLoginTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task LoginVerifiesThePasswordOnceAndRespectsEmailAndTwoFactorRequirements(
        bool twoFactorEnabled, bool requireVerifiedEmail, bool emailConfirmed)
    {
        var hasher = new CountingPasswordHasher();
        await using var tester = Create("PasswordLogin");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.AddSingleton<IPasswordHasher<IdentityUser>>(hasher);
            services.AddSingleton<EmailService, RecordingEmailService>();
        };
        await tester.Start();
        var emailService = (RecordingEmailService)tester.GetService<EmailService>();
        // Delivery is recorded in memory; no SMTP service receives these test messages.
        await emailService.SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = "smtp.example.invalid", Port = 587, Username = "test", Password = "test",
            From = "test@example.com"
        });
        await using (var conn = await tester.GetService<DBConnectionFactory>().Open())
        {
            await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, requireVerifiedEmail ? "true" : "false");
            await tester.GetService<AdminSettingsCache>().RefreshIsVerifiedEmailRequiredForLogin(conn);
        }

        var email = $"login-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, "123456", confirmEmail: emailConfirmed, githubVerified: false);
        if (twoFactorEnabled)
        {
            using var scope = tester.WebApp.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }

        using var client = HttpTestHelpers.CreateBrowser(tester);
        using (var wrong = await Login(client, email, "wrong-password"))
        {
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("Invalid login attempt.", await wrong.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, hasher.Verifications);
        Assert.Equal(0, emailService.Deliveries);
        await AssertNoSessionOrPendingSecondFactor(client);
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Equal(1, await users.GetAccessFailedCountAsync((await users.FindByEmailAsync(email))!));
        }

        hasher.Verifications = 0;
        using (var correct = await Login(client, email, "123456"))
        {
            Assert.Equal(1, hasher.Verifications);
            if (requireVerifiedEmail && !emailConfirmed)
            {
                Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
                Assert.Equal(1, emailService.Deliveries);
                await AssertNoSessionOrPendingSecondFactor(client);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Redirect, correct.StatusCode);
                Assert.Equal(0, emailService.Deliveries);
                if (twoFactorEnabled)
                {
                    Assert.StartsWith("/login/2fa", correct.Headers.Location!.OriginalString);
                    using var session = await client.GetAsync("/account/details");
                    Assert.Equal(HttpStatusCode.Redirect, session.StatusCode);
                    using var secondFactor = await client.GetAsync("/login/2fa");
                    Assert.Equal(HttpStatusCode.OK, secondFactor.StatusCode);
                }
                else
                {
                    using var session = await client.GetAsync("/account/details");
                    Assert.Equal(HttpStatusCode.OK, session.StatusCode);
                }
            }
        }

        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.SetLockoutEndDateAsync((await users.FindByEmailAsync(email))!, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
        }
        hasher.Verifications = 0;
        var previousDeliveries = emailService.Deliveries;
        using var lockedClient = HttpTestHelpers.CreateBrowser(tester);
        using var locked = await Login(lockedClient, email, "123456");
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains("Invalid login attempt.", await locked.Content.ReadAsStringAsync());
        Assert.Equal(0, hasher.Verifications);
        Assert.Equal(previousDeliveries, emailService.Deliveries);
        await AssertNoSessionOrPendingSecondFactor(lockedClient);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string email, string password)
    {
        var page = await client.GetStringAsync("/login");
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = password,
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        }));
    }

    private static async Task AssertNoSessionOrPendingSecondFactor(HttpClient client)
    {
        foreach (var path in new[] { "/account/details", "/login/2fa" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        }
    }

    private sealed class CountingPasswordHasher : PasswordHasher<IdentityUser>
    {
        public int Verifications { get; set; }

        public override PasswordVerificationResult VerifyHashedPassword(IdentityUser user, string hashedPassword, string providedPassword)
        {
            Verifications++;
            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class RecordingEmailService(DBConnectionFactory connectionFactory, AdminSettingsCache settings, ILogger<EmailService> logger)
        : EmailService(connectionFactory, settings, logger)
    {
        public int Deliveries { get; private set; }

        protected override Task<List<string>> DeliverEmail(IEnumerable<InternetAddress> recipients, string subject, string body, CancellationToken cancellationToken = default)
        {
            Deliveries++;
            return Task.FromResult(recipients.Select(r => r.ToString()).ToList());
        }
    }
}
