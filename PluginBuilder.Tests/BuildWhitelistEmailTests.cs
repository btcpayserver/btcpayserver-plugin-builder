using Dapper;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PluginBuilder.Controllers;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.Util;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildWhitelistEmailTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task ConfirmedEmailChangePreservesAccountAuthorizationAndShowsCurrentEmail()
    {
        await using var tester = CreateEmailTester();
        await tester.Start();
        var change = await PrepareEmailChange(tester);
        using var adminScope = tester.WebApp.Services.CreateScope();
        var adminUserManager = adminScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        // Cookie validation may track the administrator before a concurrent email confirmation completes.
        var trackedUser = await adminUserManager.FindByIdAsync(change.UserId);
        Assert.NotNull(trackedUser);
        Assert.Equal(change.OldEmail, trackedUser.Email);
        var admin = CreateAdminController(adminScope.ServiceProvider, change.UserId);
        var before = Assert.IsType<SettingsEditorViewModel>(Assert.IsType<ViewResult>(await admin.SettingsEditor()).Model);

        await AssertConfirmation(tester, change, change.Token, success: true);
        Assert.Equal(change.OldEmail, trackedUser.Email);

        var current = Assert.IsType<SettingsEditorViewModel>(Assert.IsType<ViewResult>(await admin.SettingsEditor()).Model);
        var currentValue = Assert.Single(current.Settings, setting => setting.key == SettingsKeys.NewBuildsWhitelist).value;
        Assert.Equal(new[] { change.NewEmail, change.OtherEmail }.Order(), currentValue.Split(',').Order());
        Assert.NotEqual(before.WhitelistVersion, current.WhitelistVersion);
        Assert.IsType<RedirectToActionResult>(await admin.SettingsEditor(
            SettingsKeys.NewBuildsWhitelist, currentValue, current.WhitelistVersion));
        Assert.False(admin.TempData.ContainsKey(TempDataConstant.WarningMessage));

        using var scope = tester.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(change.UserId);
        Assert.NotNull(user);
        Assert.Equal(change.NewEmail, user.Email);
        Assert.Equal(change.NewEmail, user.UserName);
        Assert.Equal(userManager.NormalizeEmail(change.NewEmail), user.NormalizedEmail);
        Assert.Equal(userManager.NormalizeName(change.NewEmail), user.NormalizedUserName);
        Assert.True(user.EmailConfirmed);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var details = await conn.GetAccountDetailSettings(change.UserId);
        Assert.NotNull(details);
        Assert.True(string.IsNullOrEmpty(details.PendingNewEmail));
        Assert.Equal("preserved-profile", details.Github);
        Assert.Equal(new[] { change.UserId, change.OtherUserId }.Order(),
            (await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist")).Order());
        Assert.True(await scope.ServiceProvider.GetRequiredService<BuildAccessLogic>().IsWhitelisted(Principal(change.UserId)));

        var replacementId = await tester.CreateFakeUserAsync(change.OldEmail);
        Assert.NotEqual(change.UserId, replacementId);
        Assert.False(await scope.ServiceProvider.GetRequiredService<BuildAccessLogic>().IsWhitelisted(Principal(replacementId)));

        // An editor opened before the email change must not grant the newly registered account.
        Assert.IsType<RedirectToActionResult>(await admin.SettingsEditor(
            SettingsKeys.NewBuildsWhitelist, $"{change.OldEmail},{change.OtherEmail}", before.WhitelistVersion));
        Assert.Contains("reload", Assert.IsType<string>(admin.TempData[TempDataConstant.WarningMessage]), StringComparison.OrdinalIgnoreCase);
        await AssertWhitelist(tester, change.NewEmail, change.OtherEmail);
    }

    [Fact]
    public async Task EmailConfirmationWaitsForWhitelistSnapshotBeforeEmailsAreResolved()
    {
        var normalizer = new PausingEmailNormalizer();
        await using var tester = CreateEmailTester(normalizer);
        await tester.Start();
        var change = await PrepareEmailChange(tester);
        using var adminScope = tester.WebApp.Services.CreateScope();
        var admin = CreateAdminController(adminScope.ServiceProvider, change.UserId);
        var before = Assert.IsType<SettingsEditorViewModel>(Assert.IsType<ViewResult>(await admin.SettingsEditor()).Model);
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();

        // Pause after the version was checked but before the later account-resolution query can take its own locks.
        normalizer.Arm(change.OldEmail);
        var save = Task.Run(() => admin.SettingsEditor(
            SettingsKeys.NewBuildsWhitelist, $"{change.OldEmail},{change.OtherEmail}", before.WhitelistVersion));
        Task? confirmation = null;
        try
        {
            await normalizer.Paused.WaitAsync(TimeSpan.FromSeconds(10));
            var savePid = await observer.QuerySingleAsync<int>(
                """
                SELECT DISTINCT l.pid
                FROM pg_locks l
                JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE a.datname = current_database()
                    AND l.relation = 'build_whitelist'::regclass
                    AND l.mode = 'ShareRowExclusiveLock' AND l.granted
                """);
            confirmation = AssertConfirmation(tester, change, change.Token, success: true);
            using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition(
                       """
                       SELECT EXISTS (
                           SELECT 1 FROM pg_stat_activity
                           WHERE datname = current_database()
                               AND @savePid = ANY(pg_blocking_pids(pid))
                               AND query LIKE '%UPDATE "AspNetUsers"%'
                       )
                       """, new { savePid }, cancellationToken: waitTimeout.Token)))
            {
                Assert.False(confirmation.IsCompleted,
                    "The email confirmation must wait for the whitelist snapshot, before email resolution starts.");
                await Task.Delay(10, waitTimeout.Token);
            }

            normalizer.Release();
            Assert.IsType<RedirectToActionResult>(await save.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(admin.TempData.ContainsKey(TempDataConstant.WarningMessage));
            await confirmation.WaitAsync(TimeSpan.FromSeconds(10));

            using var scope = tester.WebApp.Services.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByIdAsync(change.UserId);
            Assert.NotNull(user);
            Assert.Equal(change.NewEmail, user.Email);
            Assert.Equal(change.NewEmail, user.UserName);
            var details = await observer.GetAccountDetailSettings(change.UserId);
            Assert.NotNull(details);
            Assert.True(string.IsNullOrEmpty(details.PendingNewEmail));
            await AssertWhitelist(tester, change.NewEmail, change.OtherEmail);
            Assert.Equal(new[] { change.UserId, change.OtherUserId }.Order(),
                (await observer.QueryAsync<string>("SELECT user_id FROM build_whitelist")).Order());

            var replacementId = await tester.CreateFakeUserAsync(change.OldEmail);
            var access = scope.ServiceProvider.GetRequiredService<BuildAccessLogic>();
            Assert.True(await access.IsWhitelisted(Principal(change.UserId)));
            Assert.False(await access.IsWhitelisted(Principal(replacementId)));
        }
        finally
        {
            normalizer.Release();
            foreach (var pending in new Task?[] { save, confirmation })
                if (pending is not null)
                    try
                    {
                        await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                        // Preserve the original failure while releasing both operations before disposing the server.
                    }
        }
    }

    [Fact]
    public async Task InvalidTokenPreservesAccountPendingEmailAndWhitelist()
    {
        await using var tester = CreateEmailTester();
        await tester.Start();
        var change = await PrepareEmailChange(tester);
        var before = await ReadAccount(tester, change.UserId);

        await AssertConfirmation(tester, change, "not-a-valid-token", success: false);

        Assert.Equal(before, await ReadAccount(tester, change.UserId));
        await AssertWhitelist(tester, change.OldEmail, change.OtherEmail);
    }

    [Fact]
    public async Task RevocationWhileEmailChangeIsPendingDoesNotRestoreWhitelistAccess()
    {
        await using var tester = CreateEmailTester();
        await tester.Start();
        var change = await PrepareEmailChange(tester);
        await using (var conn = await tester.GetService<DBConnectionFactory>().Open())
            await conn.ExecuteAsync("DELETE FROM build_whitelist WHERE user_id = @UserId", new { change.UserId });

        await AssertConfirmation(tester, change, change.Token, success: true);

        using var scope = tester.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(change.UserId);
        Assert.NotNull(user);
        Assert.Equal(change.NewEmail, user.Email);
        Assert.Equal(change.NewEmail, user.UserName);
        await using var connAfter = await tester.GetService<DBConnectionFactory>().Open();
        var details = await connAfter.GetAccountDetailSettings(change.UserId);
        Assert.NotNull(details);
        Assert.True(string.IsNullOrEmpty(details.PendingNewEmail));
        await AssertWhitelist(tester, change.OtherEmail);
        Assert.False(await scope.ServiceProvider.GetRequiredService<BuildAccessLogic>().IsWhitelisted(Principal(change.UserId)));
    }

    [Fact]
    public async Task UsernameConflictRollsBackEmailChangePendingEmailAndWhitelist()
    {
        await using var tester = CreateEmailTester();
        await tester.Start();
        var change = await PrepareEmailChange(tester);

        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var conflict = new IdentityUser
            {
                UserName = change.NewEmail,
                Email = $"username-conflict-{Guid.NewGuid():N}@example.com",
                EmailConfirmed = true
            };
            var created = await userManager.CreateAsync(conflict, "123456");
            Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(error => error.Description)));
            Assert.Null(await userManager.FindByEmailAsync(change.NewEmail));
        }
        var before = await ReadAccount(tester, change.UserId);

        await AssertConfirmation(tester, change, change.Token, success: false);

        Assert.Equal(before, await ReadAccount(tester, change.UserId));
        await AssertWhitelist(tester, change.OldEmail, change.OtherEmail);
    }

    [Fact]
    public async Task ExistingEmailWithDifferentUsernameDoesNotTransferAccountOrWhitelist()
    {
        await using var tester = CreateEmailTester();
        await tester.Start();
        var change = await PrepareEmailChange(tester);

        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var conflict = new IdentityUser
            {
                UserName = $"email-conflict-{Guid.NewGuid():N}",
                Email = change.NewEmail,
                EmailConfirmed = true
            };
            var created = await userManager.CreateAsync(conflict, "123456");
            Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(error => error.Description)));
            Assert.Null(await userManager.FindByNameAsync(change.NewEmail));
        }
        var before = await ReadAccount(tester, change.UserId);

        await AssertConfirmation(tester, change, change.Token, success: false);

        Assert.Equal(before, await ReadAccount(tester, change.UserId));
        await AssertWhitelist(tester, change.OldEmail, change.OtherEmail);
    }

    private ServerTester CreateEmailTester(ILookupNormalizer? normalizer = null)
    {
        var tester = Create("BuildWhitelistEmail");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService) &&
                         (descriptor.ImplementationType == typeof(DockerStartupHostedService) ||
                          descriptor.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(descriptor);
            if (normalizer is not null)
                services.AddSingleton(normalizer);
        };
        return tester;
    }

    private sealed class PausingEmailNormalizer : ILookupNormalizer
    {
        private readonly UpperInvariantLookupNormalizer _inner = new();
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _email;
        private int _pauseTaken;

        public Task Paused => _paused.Task;
        public void Arm(string email) => _email = email;
        public void Release() => _released.TrySetResult();
        public string? NormalizeName(string? name) => _inner.NormalizeName(name);

        public string? NormalizeEmail(string? email)
        {
            if (email is not null && email == _email && Interlocked.CompareExchange(ref _pauseTaken, 1, 0) == 0)
            {
                _paused.TrySetResult();
                _released.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            return _inner.NormalizeEmail(email);
        }
    }

    private static async Task<PendingEmailChange> PrepareEmailChange(ServerTester tester)
    {
        var oldEmail = $"old-{Guid.NewGuid():N}@example.com";
        var newEmail = $"new-{Guid.NewGuid():N}@example.com";
        var otherEmail = $"other-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(oldEmail);
        var otherUserId = await tester.CreateFakeUserAsync(otherEmail);

        using var scope = tester.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByIdAsync(userId);
        Assert.NotNull(user);
        var token = await userManager.GenerateChangeEmailTokenAsync(user, newEmail);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SetAccountDetailSettings(new AccountSettings
        {
            Github = "preserved-profile",
            PendingNewEmail = newEmail
        }, userId);
        await conn.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES (@userId), (@otherUserId)",
            new { userId, otherUserId });
        return new PendingEmailChange(userId, oldEmail, newEmail, otherUserId, otherEmail, token);
    }

    private static async Task AssertConfirmation(
        ServerTester tester, PendingEmailChange change, string token, bool success)
    {
        using var client = tester.CreateHttpClient();
        using var response = await client.GetAsync(
            $"/UpdateEmail?uid={Uri.EscapeDataString(change.UserId)}&token={Uri.EscapeDataString(token)}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(success ? "has been verified!" : "We couldn't verify your email", html, StringComparison.Ordinal);
    }

    private static async Task<string> ReadAccount(ServerTester tester, string userId)
    {
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        return await conn.QuerySingleAsync<string>(
            """
            SELECT row_to_json(account)::text FROM "AspNetUsers" account WHERE "Id" = @userId
            """,
            new { userId });
    }

    private static async Task AssertWhitelist(ServerTester tester, params string[] expectedEmails)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var controller = CreateAdminController(scope.ServiceProvider, "test-administrator");
        var view = Assert.IsType<SettingsEditorViewModel>(Assert.IsType<ViewResult>(await controller.SettingsEditor()).Model);
        var value = Assert.Single(view.Settings, setting => setting.key == SettingsKeys.NewBuildsWhitelist).value;
        var actual = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(email => email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(expectedEmails.OrderBy(email => email, StringComparer.OrdinalIgnoreCase), actual,
            StringComparer.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, Roles.ServerAdmin)], "test"));

    private static AdminController CreateAdminController(IServiceProvider services, string userId)
    {
        var controller = ActivatorUtilities.CreateInstance<AdminController>(services);
        controller.ControllerContext = new ControllerContext
        {
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor(),
            HttpContext = new DefaultHttpContext { RequestServices = services, User = Principal(userId) }
        };
        return controller;
    }

    private sealed record PendingEmailChange(
        string UserId, string OldEmail, string NewEmail, string OtherUserId, string OtherEmail, string Token);
}
