using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Npgsql;
using PluginBuilder.Configuration;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.JsonConverters;
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
    AzureStorageClient azureStorageClient,
    EmailService emailService,
    AdminSettingsCache adminSettingsCache,
    ReferrerNavigationService referrerNavigation,
    PluginBuilderOptions pbOptions,
    IOutputCacheStore outputCacheStore)

    : Controller
{
    // settings editor
    private const string ProtectedKeys = SettingsKeys.EmailSettings;

    [HttpGet("plugins")]
    public async Task<IActionResult> ListPlugins(AdminPluginSettingViewModel? model = null)
    {
        model ??= new AdminPluginSettingViewModel();
        var whereConditions = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrEmpty(model.SearchText))
        {
            whereConditions.Add("(p.slug ILIKE @searchText OR u.\"Email\" ILIKE @searchText OR p.settings->>'pluginTitle' ILIKE @searchText)");
            parameters.Add("searchText", $"%{model.SearchText}%");
        }
        if (!string.IsNullOrEmpty(model.Status) && Enum.TryParse<PluginVisibilityEnum>(model.Status, true, out var statusEnum))
        {
            whereConditions.Add("p.visibility = CAST(@status AS plugin_visibility_enum)");
            parameters.Add("status", statusEnum.ToString().ToLower());
        }
        var whereClause = whereConditions.Count > 0 ? "WHERE " + string.Join(" AND ", whereConditions) : "";
        parameters.Add("skip", model.Skip);
        parameters.Add("take", model.Count);

        await using var conn = await connectionFactory.Open();
        var rows = await conn.QueryAsync($"""
                                         SELECT p.slug, p.visibility, v.ver, v.build_id, v.btcpay_min_ver, v.pre_release, v.updated_at, u."Email" as email, p.settings,
                                                EXISTS(SELECT 1 FROM plugin_listing_requests lr WHERE lr.plugin_slug = p.slug AND lr.status = 'pending') as has_pending_request
                                         FROM plugins p
                                         LEFT JOIN users_plugins up ON p.slug = up.plugin_slug AND up.is_primary_owner IS TRUE
                                         LEFT JOIN "AspNetUsers" u ON up.user_id = u."Id"
                                         LEFT JOIN (
                                             SELECT DISTINCT ON (plugin_slug) plugin_slug, ver, build_id, btcpay_min_ver, pre_release, updated_at
                                             FROM versions
                                             ORDER BY plugin_slug, updated_at DESC
                                         ) v ON p.slug = v.plugin_slug
                                         {whereClause}
                                         ORDER BY p.slug
                                         OFFSET @skip LIMIT @take;
                                         """, parameters);
        List<AdminPluginViewModel> plugins = new();

        foreach (var row in rows)
        {
            var pluginSettings = SafeJson.Deserialize<PluginSettings>((string)row.settings);
            AdminPluginViewModel plugin = new()
            {
                PluginSlug = row.slug,
                Visibility = row.visibility,
                PrimaryOwnerEmail = row.email,
                HasPendingListingRequest = row.has_pending_request,
                PluginTitle = pluginSettings?.PluginTitle
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
    public async Task<IActionResult> UpdateVerifiedEmailForPublishRequirement(bool verifiedEmailForPluginPublish)
    {
        await using var conn = await connectionFactory.Open();
        await conn.UpdateVerifiedEmailForPluginPublishSetting(verifiedEmailForPluginPublish);
        await adminSettingsCache.RefreshIsVerifiedEmailRequiredForPublish(conn);
        TempData[TempDataConstant.SuccessMessage] = "Email requirement setting for publishing plugin updated successfully";
        return RedirectToAction("ListPlugins");
    }


    [HttpGet("plugins/edit/{pluginSlug}")]
    public async Task<IActionResult> PluginEdit(string pluginSlug)
    {
        referrerNavigation.StoreReferrer();
        await using var conn = await connectionFactory.Open();
        var pluginUsers = await GetPluginUsers(conn, pluginSlug);

        var plugin = await conn.GetPluginDetails(pluginSlug);
        if (plugin == null)
            return NotFound();

        return View(new PluginEditViewModel
        {
            PluginSlug = plugin.PluginSlug,
            Identifier = plugin.Identifier,
            Settings = plugin.Settings,
            Visibility = plugin.Visibility,
            PluginUsers = pluginUsers,
            PluginSettings = SafeJson.Deserialize<PluginSettings>(plugin.Settings)
        });
    }


    [HttpPost("plugins/edit/{pluginSlug}")]
    public async Task<IActionResult> PluginEdit(string pluginSlug, PluginEditViewModel model, [FromForm] bool removeLogoFile = false)
    {
        await using var conn = await connectionFactory.Open();
        if (!ModelState.IsValid)
        {
            model.PluginUsers = await GetPluginUsers(conn, pluginSlug);
            return View(model);
        }
        var plugin = await conn.GetPluginDetails(pluginSlug);
        var pluginSettings = SafeJson.Deserialize<PluginSettings>(plugin?.Settings);
        pluginSettings.PluginTitle = model.PluginSettings.PluginTitle;
        pluginSettings.Description = model.PluginSettings.Description;
        pluginSettings.GitRepository = model.PluginSettings.GitRepository;
        pluginSettings.GitRef = model.PluginSettings.GitRef;
        pluginSettings.BuildConfig = model.PluginSettings.BuildConfig;
        pluginSettings.Documentation = model.PluginSettings.Documentation;
        pluginSettings.PluginDirectory = model.PluginSettings.PluginDirectory;
        if (model.LogoFile != null)
        {
            if (!model.LogoFile.ValidateUploadedImage(out string errorMessage))
            {
                ModelState.AddModelError(nameof(model.LogoFile), $"Image upload validation failed: {errorMessage}");
                model.PluginUsers = await GetPluginUsers(conn, pluginSlug);
                return View(model);
            }
            try
            {
                var uniqueBlobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(model.LogoFile.FileName)}";
                pluginSettings.Logo = await azureStorageClient.UploadImageFile(model.LogoFile, uniqueBlobName);
            }
            catch (Exception)
            {
                ModelState.AddModelError(nameof(model.PluginSettings.Logo), "Could not complete settings upload. An error occurred while uploading logo");
                model.PluginUsers = await GetPluginUsers(conn, pluginSlug);
                return View(model);
            }
        }
        else if (removeLogoFile)
        {
            model.LogoFile = null;
            pluginSettings.Logo = null;
        }

        var setPluginSettings = await conn.SetPluginSettings(pluginSlug, pluginSettings, model.Visibility);
        if (!setPluginSettings) return NotFound();

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        TempData[TempDataConstant.SuccessMessage] = "Plugin settings updated successfully";
        return referrerNavigation.RedirectToReferrerOr(this, "ListPlugins");
    }


    [HttpGet("plugins/delete/{routeSlug}")]
    public async Task<IActionResult> PluginDelete(string routeSlug)
    {
        referrerNavigation.StoreReferrer();

        await using var conn = await connectionFactory.Open();
        var plugin = await conn.GetPluginDetails(routeSlug);
        if (plugin == null) return NotFound();

        return View(plugin);
    }

    [HttpPost("plugins/delete/{routeSlug}")]
    public async Task<IActionResult> PluginDeleteConfirmed(string routeSlug)
    {
        await using var conn = await connectionFactory.Open();
        var affectedRows = await conn.ExecuteAsync("""
                                                   DELETE FROM builds WHERE plugin_slug = @slug;
                                                   DELETE FROM builds_ids WHERE plugin_slug = @slug;
                                                   DELETE FROM builds_logs WHERE plugin_slug = @slug;
                                                   DELETE FROM users_plugins WHERE plugin_slug = @slug;
                                                   DELETE FROM versions WHERE plugin_slug = @slug;
                                                   DELETE FROM plugins WHERE slug = @slug;
                                                   """, new { slug = routeSlug });
        if (affectedRows == 0) return NotFound();

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        return referrerNavigation.RedirectToReferrerOr(this,"ListPlugins");
    }

    [HttpPost("plugins/{pluginSlug}/ownership")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManagePluginOwnership(string pluginSlug, string userId, string command = "")
    {
        await using var conn = await connectionFactory.Open();
        var userIds = await conn.RetrievePluginUserIds(pluginSlug);
        if (!userIds.Contains(userId))
        {
            TempData[TempDataConstant.WarningMessage] = "Invalid plugin user";
            return RedirectToAction(nameof(PluginEdit), new { pluginSlug });
        }
        switch (command)
        {
            case "RevokeOwnership":
                {
                    var ok = await conn.RevokePluginPrimaryOwnership(pluginSlug, userId);
                    if (!ok)
                    {
                        TempData[TempDataConstant.WarningMessage] = "Error revoking primary ownership";
                        return RedirectToAction(nameof(PluginEdit), new { pluginSlug });
                    }
                    TempData[TempDataConstant.SuccessMessage] = "Primary ownership revoked";
                    break;
                }
            case "AssignOwnership":
                {
                    var ok = await conn.AssignPluginPrimaryOwner(pluginSlug, userId);
                    if (!ok)
                    {
                        TempData[TempDataConstant.WarningMessage] = "Error assigning primary ownership";
                        return RedirectToAction(nameof(PluginEdit), new { pluginSlug });
                    }
                    TempData[TempDataConstant.SuccessMessage] = "Primary owner assigned";
                    break;
                }
        }
        return RedirectToAction(nameof(PluginEdit), new { pluginSlug });
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
                await emailService.SaveEmailSettingsToDatabase(model);
                TempData[TempDataConstant.SuccessMessage] = "SMTP password reset.";
                return RedirectToAction(nameof(EmailSettings));
            }
        }
        if (!ModelState.IsValid) return View(model);

        if (!await ValidateSmtpConnection(model)) return View(model);

        await emailService.SaveEmailSettingsToDatabase(model);
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

    [HttpGet("emailsender")]
    public async Task<IActionResult> EmailSender(string to, string subject, string message)
    {
        if (!string.IsNullOrEmpty(to) && !emailService.IsValidEmailList(to))
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
        if (!emailService.IsValidEmailList(model.To))
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
        await adminSettingsCache.RefreshAllAdminSettings(conn);
        return RedirectToAction(nameof(SettingsEditor));
    }

    [HttpDelete("SettingsEditor")]
    public async Task<IActionResult> SettingsEditorDelete(string key)
    {
        if (key == ProtectedKeys)
            return BadRequest();

        await using var conn = await connectionFactory.Open();
        var result = await conn.SettingsDeleteAsync(key);
        await adminSettingsCache.RefreshAllAdminSettings(conn);
        return Ok();
    }

    [HttpGet("server/logs/{file?}")]
    public async Task<IActionResult> LogsView(string? file = null, int offset = 0, bool download = false)
    {
        if (offset < 0) offset = 0;

        var vm = new LogsViewModel();

        if (string.IsNullOrEmpty(pbOptions.DebugLogFile))
        {
            TempData[TempDataConstant.WarningMessage] = "File Logging Option not specified. You need to set debuglog and optionally debugloglevel in the configuration or through runtime arguments";
            return View("Logs", vm);
        }

        var logsDirectory = Directory.GetParent(pbOptions.DebugLogFile);
        if (logsDirectory is null)
        {
            TempData[TempDataConstant.WarningMessage] = "Could not load log files";
            return View("Logs", vm);
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(pbOptions.DebugLogFile);
        var fileExtension = Path.GetExtension(pbOptions.DebugLogFile);

        var allFiles = logsDirectory.GetFiles($"{fileNameWithoutExtension}*{fileExtension}")
                         .OrderByDescending(info => info.LastWriteTime)
                         .ToList();

        vm.LogFileCount = allFiles.Count;
        vm.LogFiles = allFiles.Skip(offset).Take(5).ToList();
        vm.LogFileOffset = offset;

        if (string.IsNullOrEmpty(file) || !file.EndsWith(fileExtension, StringComparison.Ordinal))
            return View("Logs", vm);

        var selectedFile = allFiles.FirstOrDefault(f => f.Name.Equals(file, StringComparison.Ordinal));
        if (selectedFile is null)
            return NotFound();

        try
        {
            var fileStream = new FileStream(selectedFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (download)
            {
                return File(fileStream, "text/plain", file, enableRangeProcessing: true);
            }
            await using (fileStream)
            using (var reader = new StreamReader(fileStream))
            {
                vm.Log = await reader.ReadToEndAsync();
            }
        }
        catch
        {
            return NotFound();
        }
        return View("Logs", vm);
    }

    private async Task<List<PluginUsersViewModel>> GetPluginUsers(NpgsqlConnection conn, string pluginSlug)
    {
        var ownerId = await conn.RetrievePluginPrimaryOwner(pluginSlug);
        var userIds = (await conn.RetrievePluginUserIds(pluginSlug)).ToList();

        if (userIds.Count == 0)
            return new List<PluginUsersViewModel>();

        var users = await userManager.FindUsersByIdsAsync(userIds);
        return users.Select(u => new PluginUsersViewModel
        {
            Email = u.Email ?? string.Empty,
            UserId = u.Id,
            IsPluginOwner = u.Id == ownerId
        }).ToList();
    }

    #region Listing Requests Management

    [HttpGet("listing-requests")]
    public async Task<IActionResult> ListingRequests(string? status = null)
    {
        await using var conn = await connectionFactory.Open();
        
        var statusFilter = status?.ToLowerInvariant() ?? "pending";
        var sql = """
            SELECT 
                lr.id AS "Id",
                lr.plugin_slug AS "PluginSlug",
                lr.status AS "Status",
                lr.submitted_at AS "SubmittedAt",
                lr.announcement_date AS "AnnouncementDate",
                p.settings->>'pluginTitle' AS "PluginTitle",
                u."Email" AS "PrimaryOwnerEmail"
            FROM plugin_listing_requests lr
            JOIN plugins p ON lr.plugin_slug = p.slug
            LEFT JOIN users_plugins up ON p.slug = up.plugin_slug AND up.is_primary_owner = true
            LEFT JOIN "AspNetUsers" u ON up.user_id = u."Id"
            WHERE (@status = 'all' OR lr.status = @status)
            ORDER BY 
                CASE WHEN lr.status = 'pending' THEN 0 ELSE 1 END,
                lr.submitted_at DESC
            """;

        var requests = await conn.QueryAsync<ListingRequestItemViewModel>(sql, new { status = statusFilter });
        
        var vm = new ListingRequestsViewModel
        {
            Requests = requests.ToList(),
            StatusFilter = statusFilter
        };

        return View(vm);
    }

    [HttpGet("listing-requests/{requestId}")]
    public async Task<IActionResult> ListingRequestDetail(int requestId)
    {
        await using var conn = await connectionFactory.Open();
        
        var request = await conn.GetListingRequest(requestId);
        if (request == null)
            return NotFound();

        var plugin = await conn.GetPluginDetails(new PluginSlug(request.PluginSlug));
        var pluginSettings = SafeJson.Deserialize<PluginSettings>(plugin?.Settings);
        var owners = await conn.GetPluginOwners(new PluginSlug(request.PluginSlug));
        
        var ownerVerifications = new List<OwnerVerificationViewModel>();
        foreach (var owner in owners)
        {
            var accountSettings = await conn.GetAccountDetailSettings(owner.UserId);
            ownerVerifications.Add(new OwnerVerificationViewModel
            {
                Email = owner.Email ?? string.Empty,
                IsPrimary = owner.IsPrimary,
                EmailVerified = owner.EmailConfirmed,
                GithubVerified = accountSettings?.Github != null,
                NostrVerified = accountSettings?.Nostr?.Npub != null
            });
        }

        var reviewedByEmail = request.ReviewedBy != null 
            ? (await userManager.FindByIdAsync(request.ReviewedBy))?.Email 
            : null;

        var vm = new ListingRequestDetailViewModel
        {
            Id = request.Id,
            PluginSlug = request.PluginSlug,
            PluginTitle = pluginSettings?.PluginTitle,
            PluginDescription = pluginSettings?.Description,
            Logo = pluginSettings?.Logo,
            GitRepository = pluginSettings?.GitRepository,
            Documentation = pluginSettings?.Documentation,
            ReleaseNote = request.ReleaseNote,
            TelegramVerificationMessage = request.TelegramVerificationMessage,
            UserReviews = request.UserReviews,
            AnnouncementDate = request.AnnouncementDate,
            Status = request.Status,
            SubmittedAt = request.SubmittedAt,
            ReviewedAt = request.ReviewedAt,
            ReviewedByEmail = reviewedByEmail,
            RejectionReason = request.RejectionReason,
            Owners = ownerVerifications,
            PrimaryOwnerEmail = owners.FirstOrDefault(o => o.IsPrimary)?.Email ?? "Unknown"
        };

        return View(vm);
    }

    [HttpPost("listing-requests/{requestId}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveListingRequest(int requestId)
    {
        await using var conn = await connectionFactory.Open();
        
        var request = await conn.GetListingRequest(requestId);
        if (request == null)
            return NotFound();

        if (request.Status != PluginListingRequestStatus.Pending)
        {
            TempData[TempDataConstant.WarningMessage] = "This request has already been processed";
            return RedirectToAction(nameof(ListingRequestDetail), new { requestId });
        }

        var userId = userManager.GetUserId(User)!;
        
        // Approve the request
        await conn.ApproveListingRequest(requestId, userId);
        
        // Set plugin visibility to listed
        await conn.SetPluginSettings(new PluginSlug(request.PluginSlug), null, "listed");
        
        // Clear cache
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        
        TempData[TempDataConstant.SuccessMessage] = $"Plugin '{request.PluginSlug}' has been approved and is now listed";
        return RedirectToAction(nameof(ListingRequests));
    }

    [HttpPost("listing-requests/{requestId}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectListingRequest(int requestId, string rejectionReason)
    {
        await using var conn = await connectionFactory.Open();
        
        var request = await conn.GetListingRequest(requestId);
        if (request == null)
            return NotFound();

        if (request.Status != PluginListingRequestStatus.Pending)
        {
            TempData[TempDataConstant.WarningMessage] = "This request has already been processed";
            return RedirectToAction(nameof(ListingRequestDetail), new { requestId });
        }

        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            TempData[TempDataConstant.WarningMessage] = "Rejection reason is required";
            return RedirectToAction(nameof(ListingRequestDetail), new { requestId });
        }

        var userId = userManager.GetUserId(User)!;
        
        // Reject the request
        await conn.RejectListingRequest(requestId, userId, rejectionReason.Trim());
        
        TempData[TempDataConstant.SuccessMessage] = $"Plugin listing request for '{request.PluginSlug}' has been rejected";
        return RedirectToAction(nameof(ListingRequests));
    }

    #endregion
}
