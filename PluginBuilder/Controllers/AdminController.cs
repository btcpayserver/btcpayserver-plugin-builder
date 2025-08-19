using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Controllers;

[Authorize(Roles = Roles.ServerAdmin)]
[Route("/admin/")]
public class AdminController(
    UserManager<IdentityUser> userManager,
    RoleManager<IdentityRole> roleManager,
    DBConnectionFactory connectionFactory,
    EmailService emailService,
    EmailVerifiedCache emailVerifiedCache,
    ReferrerNavigationService referrerNavigation)
    : Controller
{
    // settings editor
    private const string ProtectedKeys = SettingsKeys.EmailSettings;

    [HttpGet("plugins")]
    public async Task<IActionResult> ListPlugins(AdminPluginSettingViewModel? model = null)
    {
        model ??= new AdminPluginSettingViewModel();
        await using var conn = await connectionFactory.Open();
        var rows = await conn.QueryAsync("""
                                         SELECT p.slug, p.visibility, v.ver, v.build_id, v.btcpay_min_ver, v.pre_release, v.updated_at, u."Email" as email
                                         FROM plugins p
                                         LEFT JOIN users_plugins up ON p.slug = up.plugin_slug
                                         LEFT JOIN "AspNetUsers" u ON up.user_id = u."Id"
                                         LEFT JOIN (
                                             SELECT DISTINCT ON (plugin_slug) plugin_slug, ver, build_id, btcpay_min_ver, pre_release, updated_at
                                             FROM versions
                                             ORDER BY plugin_slug, updated_at DESC
                                         ) v ON p.slug = v.plugin_slug
                                         ORDER BY p.slug;
                                         """);
        List<AdminPluginViewModel> plugins = new();

        if (!string.IsNullOrEmpty(model.SearchText))
            rows = rows.Where(o => (o.slug != null && o.slug.Contains(model.SearchText)) || (o.email != null && o.email.Contains(model.SearchText)));

        if (!string.IsNullOrEmpty(model.Status) && Enum.TryParse<PluginVisibilityEnum>(model.Status, true, out var statusEnum))
            rows = rows.Where(o => o.visibility == statusEnum);

        var pluginData = rows.Skip(model.Skip).Take(model.Count);
        foreach (var row in pluginData)
        {
            AdminPluginViewModel plugin = new()
            {
                ProjectSlug = row.slug,
                Visibility = row.visibility,
                PublisherEmail = row.email
            };

            if (row.ver != null)
            {
                plugin.Version = string.Join('.', row.ver);
                plugin.BuildId = row.build_id;
                plugin.BtcPayMinVer = string.Join('.', row.btcpay_min_ver);
                plugin.PreRelease = row.pre_release;
                plugin.UpdatedAt = row.updated_at;
            }

            plugins.Add(plugin);
        }

        model.Plugins = plugins;
        model.VerifiedEmailForPluginPublish = await conn.GetVerifiedEmailForPluginPublishSetting();
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> UpdateVerifiedEmailRequirement(bool verifiedEmailForPluginPublish)
    {
        await using var conn = await connectionFactory.Open();
        await conn.UpdateVerifiedEmailForPluginPublishSetting(verifiedEmailForPluginPublish);
        await emailVerifiedCache.RefreshIsVerifiedEmailRequired(conn);
        TempData[TempDataConstant.SuccessMessage] = "Email requirement setting for publishing plugin updated successfully";
        return RedirectToAction("ListPlugins");
    }


    [HttpGet("plugins/edit/{slug}")]
    public async Task<IActionResult> PluginEdit(string slug)
    {
        referrerNavigation.StoreReferrer();
        await using var conn = await connectionFactory.Open();
        var pluginUsers = await GetPluginUsers(conn, slug);

        var plugin = await conn.QueryFirstOrDefaultAsync<PluginViewModel>(
            "SELECT * FROM plugins WHERE slug = @Slug", new { Slug = slug });
        if (plugin == null)
            return NotFound();

        return View(new PluginEditViewModel
        {
            Slug = plugin.Slug,
            Identifier = plugin.Identifier,
            Settings = plugin.Settings,
            Visibility = plugin.Visibility,
            PluginUsers = pluginUsers
        });
    }

    //
    [HttpPost("plugins/edit/{slug}")]
    public async Task<IActionResult> PluginEdit(string slug, PluginEditViewModel model)
    {
        await using var conn = await connectionFactory.Open();
        if (!ModelState.IsValid)
        {
            model.PluginUsers = await GetPluginUsers(conn, slug);
            return View(model);
        }

        var affectedRows = await conn.ExecuteAsync("""
                                                    UPDATE plugins
                                                    SET settings = @settings::JSONB, visibility = @visibility::plugin_visibility_enum
                                                    WHERE slug = @slug
                                                   """,
            new
            {
                settings = model.Settings,
                visibility = model.Visibility.ToString().ToLowerInvariant(),
                slug
            });
        if (affectedRows == 0) return NotFound();

        return referrerNavigation.RedirectToReferrerOr(this, "ListPlugins");
    }

    // Plugin Delete
    [HttpGet("plugins/delete/{slug}")]
    public async Task<IActionResult> PluginDelete(string slug)
    {
        referrerNavigation.StoreReferrer();

        await using var conn = await connectionFactory.Open();
        var plugin = await conn.QueryFirstOrDefaultAsync<PluginViewModel>(
            "SELECT * FROM plugins WHERE slug = @Slug", new { Slug = slug });
        if (plugin == null) return NotFound();

        return View(plugin);
    }

    [HttpPost("plugins/delete/{slug}")]
    public async Task<IActionResult> PluginDeleteConfirmed(string slug)
    {
        await using var conn = await connectionFactory.Open();
        var affectedRows = await conn.ExecuteAsync("""
                                                   DELETE FROM builds WHERE plugin_slug = @Slug;
                                                   DELETE FROM builds_ids WHERE plugin_slug = @Slug;
                                                   DELETE FROM builds_logs WHERE plugin_slug = @Slug;
                                                   DELETE FROM users_plugins WHERE plugin_slug = @Slug;
                                                   DELETE FROM versions WHERE plugin_slug = @Slug;
                                                   DELETE FROM plugins WHERE slug = @Slug;
                                                   """, new { Slug = slug });
        if (affectedRows == 0) return NotFound();

        return referrerNavigation.RedirectToReferrerOr(this,"ListPlugins");
    }

    [HttpGet]
    public async Task<IActionResult> ManagePluginOwnership(string pluginSlug, string userId, string command = "")
    {
        await using var conn = await connectionFactory.Open();
        var userIds = await conn.RetrievePluginUserIds(pluginSlug);
        if (!userIds.Contains(userId))
        {
            TempData[TempDataConstant.WarningMessage] = "Invalid plugin user";
            return RedirectToAction(nameof(PluginEdit), new { slug = pluginSlug });
        }
        switch (command)
        {
            case "RevokeOwnership":
                {
                    await conn.RevokePluginOwnership(pluginSlug, userId);
                    TempData[TempDataConstant.SuccessMessage] = "Plugin ownership has been revoked";
                    break;
                }
            case "AssignOwnership":
                {
                    await conn.AssignPluginPrimaryOwner(pluginSlug, userId);
                    TempData[TempDataConstant.SuccessMessage] = "Plugin owener assignment was successful";
                    break;
                }
            default:
                break;
        }
        return RedirectToAction(nameof(PluginEdit), new { slug = pluginSlug });
    }


    // list users
    [HttpGet("users")]
    public async Task<IActionResult> Users(AdminUsersListViewModel? model = null)
    {
        model ??= new AdminUsersListViewModel();
        await using var conn = await connectionFactory.Open();
        var usersQuery = userManager.Users.OrderBy(a => a.Email).AsQueryable();

        if (!string.IsNullOrEmpty(model.SearchText))
            usersQuery = usersQuery.Where(o =>
                (o.UserName != null && o.UserName.Contains(model.SearchText)) || (o.Email != null && o.Email.Contains(model.SearchText)));
        var users = usersQuery.Skip(model.Skip).Take(model.Count).ToList();
        List<AdminUsersViewModel> usersList = new();
        foreach (var user in users)
        {
            var userRoles = await userManager.GetRolesAsync(user);
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
            usersList.Add(new AdminUsersViewModel
            {
                Id = user.Id,
                Email = user.Email!,
                UserName = user.UserName!,
                EmailConfirmed = user.EmailConfirmed,
                Roles = userRoles,
                PendingNewEmail = accountSettings?.PendingNewEmail
            });
        }

        model.Users = usersList;
        return View(model);
    }

    // edit roles
    [HttpGet("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        var userRoles = await userManager.GetRolesAsync(user);
        var allRoles = roleManager.Roles.ToList();
        EditUserRolesViewModel model = new()
        {
            UserId = user.Id,
            UserName = user.UserName!,
            UserRoles = userRoles,
            AvailableRoles = allRoles
        };
        return View(model);
    }

    [HttpPost("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId, List<string> userRoles)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        var currentRoles = await userManager.GetRolesAsync(user);
        var rolesToAdd = userRoles.Except(currentRoles).ToList();
        var rolesToRemove = currentRoles.Except(userRoles).ToList();

        // Validate if this is the last admin user and prevent it from being removed from the ServerAdmin role
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (currentUserId == userId && rolesToRemove.Contains(Roles.ServerAdmin))
        {
            var admins = await userManager.GetUsersInRoleAsync(Roles.ServerAdmin);
            if (admins.Count == 1)
            {
                ModelState.AddModelError("", "You cannot remove yourself as the last ServerAdmin.");

                // Rebuild the view model to pass it back to the view
                EditUserRolesViewModel model = new()
                {
                    UserId = userId,
                    UserRoles = currentRoles.ToList(),
                    AvailableRoles = roleManager.Roles.ToList()
                };
                return View(model);
            }
        }

        await userManager.AddToRolesAsync(user, rolesToAdd);
        await userManager.RemoveFromRolesAsync(user, rolesToRemove);
        return RedirectToAction("Users");
    }

    // init reset password
    [HttpGet("/admin/userpasswordreset")]
    public async Task<IActionResult> UserPasswordReset(string userId)
    {
        InitPasswordResetViewModel model = new();
        var user = await userManager.FindByIdAsync(userId);
        if (user != null) model.Email = user.Email;

        return View(model);
    }

    [HttpPost("/admin/userpasswordreset")]
    public async Task<IActionResult> UserPasswordReset(InitPasswordResetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Require the user to have a confirmed email before they can log on.
        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
            return View(model);
        }

        var result = await userManager.GeneratePasswordResetTokenAsync(user);
        model.PasswordResetToken = result;
        return View(model);
    }

    [HttpGet("/admin/userchangeemail")]
    public async Task<IActionResult> UserChangeEmail(string userId)
    {
        await using var conn = await connectionFactory.Open();
        UserChangeEmailViewModel model = new();
        var user = await userManager.FindByIdAsync(userId);
        if (user != null)
        {
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
            model.OldEmail = user.Email;
            model.PendingNewEmail = !string.IsNullOrEmpty(accountSettings?.PendingNewEmail) ? accountSettings.PendingNewEmail : null;
        }

        return View(model);
    }

    [HttpPost("/admin/userchangeemail")]
    public async Task<IActionResult> UserChangeEmail(UserChangeEmailViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        await using var conn = await connectionFactory.Open();
        var user = await userManager.FindByEmailAsync(model.OldEmail);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
            return View(model);
        }

        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
        accountSettings.PendingNewEmail = model.NewEmail;
        await conn.SetAccountDetailSettings(accountSettings, user.Id);
        var token = await userManager.GenerateChangeEmailTokenAsync(user, model.NewEmail);
        var link = Url.Action("VerifyEmailUpdate", "Home", new { uid = user.Id, token }, Request.Scheme, Request.Host.ToString())!;
        await emailService.SendVerifyEmail(model.NewEmail, link);
        TempData[TempDataConstant.SuccessMessage] = "A verification email has been sent to user's new email address: " + model.PendingNewEmail;
        return RedirectToAction(nameof(Users));
    }

    [HttpGet("emailsettings")]
    public async Task<IActionResult> EmailSettings()
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb() ?? new EmailSettingsViewModel { Port = 465 };
        return View(emailSettings);
    }

    [HttpPost("emailsettings")]
    public async Task<IActionResult> EmailSettings(EmailSettingsViewModel model, bool passwordSet, string? command)
    {
        if (passwordSet)
        {
            var dbModel = await emailService.GetEmailSettingsFromDb();
            if (dbModel != null) model.Password = dbModel.Password;
            ModelState.Remove("Password");

            if (command?.Equals("resetpassword", StringComparison.OrdinalIgnoreCase) == true)
            {
                model.Password = null!;
                await SaveEmailSettingsToDatabase(model);
                TempData[TempDataConstant.SuccessMessage] = "SMTP password reset.";
                return RedirectToAction(nameof(EmailSettings));
            }
        }

        if (!ModelState.IsValid) return View(model);

        if (!await ValidateSmtpConnection(model)) return View(model);

        await SaveEmailSettingsToDatabase(model);
        TempData[TempDataConstant.SuccessMessage] = $"SMTP settings updated. Emails will be sent from {model.From}.";
        return RedirectToAction(nameof(EmailSettings));
    }

    private async Task<bool> ValidateSmtpConnection(EmailSettingsViewModel model)
    {
        try
        {
            var smtpClient = await emailService.CreateSmtpClient(model);
            await smtpClient.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Failed to connect to SMTP server: {ex.Message}");
            return false;
        }

        return true;
    }

    private async Task SaveEmailSettingsToDatabase(EmailSettingsViewModel model)
    {
        await using var conn = await connectionFactory.Open();
        var emailSettingsJson = JsonConvert.SerializeObject(model);
        await conn.SettingsSetAsync("EmailSettings", emailSettingsJson);
    }

    [HttpGet("emailsender")]
    public async Task<IActionResult> EmailSender(string to, string subject, string message)
    {
        if (!string.IsNullOrEmpty(to) && !IsValidEmailList(to))
        {
            ModelState.AddModelError("To", "Invalid email format in the 'To' field. Please ensure all emails are valid.");
            return View(new EmailSenderViewModel());
        }

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (emailSettings == null)
        {
            TempData[TempDataConstant.WarningMessage] = "Email testing can't be done before SMTP is set";
            return RedirectToAction(nameof(EmailSettings));
        }

        EmailSenderViewModel model = new()
        {
            From = emailSettings.From,
            To = to,
            Subject = subject,
            Message = message
        };
        return View(model);
    }

    [HttpPost("emailsender")]
    public async Task<IActionResult> EmailSender(EmailSenderViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        if (!IsValidEmailList(model.To))
        {
            ModelState.AddModelError("To", "Invalid email format in the 'To' field. Please ensure all emails are valid.");
            return View(model);
        }

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (emailSettings == null)
        {
            TempData[TempDataConstant.WarningMessage] = "Email settings not found";
            return View(model);
        }

        try
        {
            var emailsSent = await emailService.SendEmail(model.To, model.Subject, model.Message);
            TempData[TempDataConstant.SuccessMessage] = $"Emails sent successfully to {emailsSent.Count} recipient(s).";
        }
        catch (Exception ex)
        {
            TempData[TempDataConstant.WarningMessage] = $"Failed to send test email: {ex.Message}";
            return View(model);
        }

        return View(model);
    }

    private bool IsValidEmailList(string to)
    {
        return to.Split(',')
            .Select(email => email.Trim())
            .All(email => !string.IsNullOrWhiteSpace(email) && Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
    }

    [HttpGet("SettingsEditor")]
    public async Task<IActionResult> SettingsEditor()
    {
        await using var conn = await connectionFactory.Open();
        var result = await conn.SettingsGetAllAsync();
        var list = result.ToList();
        list.RemoveAll(setting => setting.key == ProtectedKeys);

        return View(list);
    }

    [HttpPost("SettingsEditor")]
    public async Task<IActionResult> SettingsEditor(string key, string value)
    {
        if (key == ProtectedKeys)
            return BadRequest();

        await using var conn = await connectionFactory.Open();
        var result = await conn.SettingsSetAsync(key, value);
        await emailVerifiedCache.RefreshIsVerifiedEmailRequired(conn);
        return RedirectToAction(nameof(SettingsEditor));
    }

    [HttpDelete("SettingsEditor")]
    public async Task<IActionResult> SettingsEditorDelete(string key)
    {
        if (key == ProtectedKeys)
            return BadRequest();

        await using var conn = await connectionFactory.Open();
        var result = await conn.SettingsDeleteAsync(key);
        await emailVerifiedCache.RefreshIsVerifiedEmailRequired(conn);
        return Ok();
    }

    private async Task<List<PluginUsersViewModel>> GetPluginUsers(NpgsqlConnection conn, string slug)
    {
        var pluginOwner = await conn.RetrievePluginOwner(slug);
        var userIds = await conn.RetrievePluginUserIds(slug);
        var users = await userManager.FindUsersByIdsAsync(userIds);
        return userIds.Any()
            ? (await userManager.FindUsersByIdsAsync(userIds)).Select(u => new PluginUsersViewModel
            {
                Email = u.Email,
                UserId = u.Id,
                IsPluginOwner = u.Id == pluginOwner
            }).ToList()
            : new List<PluginUsersViewModel>();
    }
}
