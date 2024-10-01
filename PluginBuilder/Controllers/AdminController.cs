using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Controllers;

[Authorize(Roles = "ServerAdmin")]
public class AdminController : Controller
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly DBConnectionFactory _connectionFactory;

    public AdminController(UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager,
        DBConnectionFactory connectionFactory)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _connectionFactory = connectionFactory;
    }

    [HttpGet("plugins")]
    public async Task<IActionResult> ListPlugins()
    {
        await using var conn = await _connectionFactory.Open();
        var rows = await conn.QueryAsync(
            $"""
              SELECT p.slug, v.ver, v.build_id, v.btcpay_min_ver, v.pre_release, v.updated_at, u."Email" as email
              FROM plugins p 
              JOIN versions v ON p.slug = v.plugin_slug
              JOIN users_plugins up ON v.plugin_slug = up.plugin_slug 
              JOIN "AspNetUsers" u ON up.user_id = u."Id"
              WHERE v.ver = (SELECT MAX(ver) FROM versions WHERE plugin_slug = p.slug)
              ORDER BY p.slug
              """);
        var plugins = new List<AdminPluginViewModel>();
        foreach (var row in rows)
        {
            var plugin = new AdminPluginViewModel
            {
                ProjectSlug = row.slug,
                Version = string.Join('.', row.ver),
                BuildId = row.build_id,
                BtccpayMinVer = string.Join('.', row.btcpay_min_ver),
                PreRelease = row.pre_release,
                UpdatedAt = row.updated_at,
                PublisherEmail = row.email
            };
            plugins.Add(plugin);
        }
        return View(plugins);
    }


    [Route("admin/plugins/{pluginSlug}")]
    public async Task<IActionResult> GetPlugin([ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
    {
        await using var conn = await _connectionFactory.Open();
        var pluginDetail = await conn.QueryFirstOrDefaultAsync(
            $"""
              SELECT p.slug, v.ver, v.build_id, v.btcpay_min_ver, v.pre_release, v.updated_at, u."Email" as email
              FROM plugins p 
              JOIN versions v ON p.slug = v.plugin_slug
              JOIN users_plugins up ON v.plugin_slug = up.plugin_slug 
              JOIN "AspNetUsers" u ON up.user_id = u."Id"
              WHERE p.slug = @pluginSlug  AND v.ver = (SELECT MAX(ver) FROM versions WHERE plugin_slug = p.slug)
              """, new { pluginSlug = pluginSlug.ToString() });

        var plugin = new AdminPluginViewModel
        {
            ProjectSlug = pluginDetail.slug,
            Version = string.Join('.', pluginDetail.ver),
            BuildId = pluginDetail.build_id,
            BtccpayMinVer = string.Join('.', pluginDetail.btcpay_min_ver),
            PreRelease = pluginDetail.pre_release,
            UpdatedAt = pluginDetail.updated_at,
            PublisherEmail = pluginDetail.email
        };

        var pluginBuilds = await conn.QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published, bool pre_release)>
                ("SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release " +
                "FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug = @pluginSlug " +
                "ORDER BY id DESC " +
                "LIMIT 20", new { pluginSlug = pluginSlug.ToString() });
        var vm = new BuildListViewModel();
        foreach (var row in pluginBuilds)
        {
            var b = new BuildListViewModel.BuildViewModel();
            var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
            var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
            vm.Builds.Add(b);
            b.BuildId = row.id;
            b.State = row.state;
            b.Commit = buildInfo?.GitCommit?.Substring(0, 8);
            b.Repository = buildInfo?.GitRepository;
            b.GitRef = buildInfo?.GitRef;
            b.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, pluginSlug.ToString());
            b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
            b.RepositoryLink = PluginController.GetUrl(buildInfo);
            b.DownloadLink = buildInfo?.Url;
            b.Error = buildInfo?.Error;
        }
        plugin.PluginSlugBuilds[pluginSlug.ToString()] = vm.Builds;
        return View(plugin);
    }

    [HttpGet("admin/plugins/{pluginSlug}/delete")]
    public async Task<IActionResult> DeletePlugin([ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
    {
        await using var conn = await _connectionFactory.Open();
        var plugin = await conn.QueryFirstOrDefaultAsync<string>(
            $"""
              SELECT p.slug FROM plugins p 
              WHERE p.slug = @pluginSlug
              """, new { pluginSlug = pluginSlug.ToString() });

        if (string.IsNullOrEmpty(plugin))
            return NotFound();

        return View("Confirm", new ConfirmModel("Delete user", $"The plugin and all its versions will be permanently deleted. Are you sure?", "Delete"));
    }

    [HttpPost("admin/plugins/{pluginSlug}/delete")]
    public async Task<IActionResult> DeleteUserPost([ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
    {
        await using var conn = await _connectionFactory.Open();
        var plugin = await conn.QueryFirstOrDefaultAsync<string>(
            $"""
              SELECT p.slug FROM plugins p 
              WHERE p.slug = @pluginSlug
              """, new { pluginSlug = pluginSlug.ToString() });

        if (string.IsNullOrEmpty(plugin))
            return NotFound();

        await conn.DeletePluginDetails(pluginSlug);
        return RedirectToAction(nameof(ListPlugins));
    }

    // list users
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = _userManager.Users.ToList();
        var model = new List<AdminUsersViewModel>();
        foreach (var user in users)
        {
            var userRoles = await _userManager.GetRolesAsync(user);
            model.Add(new AdminUsersViewModel
            {
                Id = user.Id,
                Email = user.Email!,
                UserName = user.UserName!,
                EmailConfirmed = user.EmailConfirmed,
                Roles = userRoles
            });
        }

        return View(model);
    }

    // edit roles
    [HttpGet("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        var userRoles = await _userManager.GetRolesAsync(user);
        var allRoles = _roleManager.Roles.ToList();
        var model = new EditUserRolesViewModel
        {
            UserId = user.Id, UserName = user.UserName, UserRoles = userRoles, AvailableRoles = allRoles
        };
        return View(model);
    }

    [HttpPost("editroles/{userId}")]
    public async Task<IActionResult> EditRoles(string userId, List<string> userRoles)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var rolesToAdd = userRoles.Except(currentRoles).ToList();
        var rolesToRemove = currentRoles.Except(userRoles).ToList();

        // Validate if this is the last admin user and prevent it from being removed from the ServerAdmin role
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (currentUserId == userId && rolesToRemove.Contains("ServerAdmin"))
        {
            var admins = await _userManager.GetUsersInRoleAsync("ServerAdmin");
            if (admins.Count == 1)
            {
                ModelState.AddModelError("", "You cannot remove yourself as the last ServerAdmin.");

                // Rebuild the view model to pass it back to the view
                var model = new EditUserRolesViewModel
                {
                    UserId = userId, UserRoles = currentRoles.ToList(), AvailableRoles = _roleManager.Roles.ToList()
                };
                return View(model);
            }
        }

        await _userManager.AddToRolesAsync(user, rolesToAdd);
        await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
        return RedirectToAction("Users");
    }

    // init reset password
    [HttpGet("/admin/initpasswordreset")]
    public async Task<IActionResult> InitPasswordReset(string userId)
    {
        var model = new InitPasswordResetViewModel();
        var user = await _userManager.FindByIdAsync(userId);
        if (user != null)
        {
            model.Email = user.Email;
        }

        return View(model);
    }

    [HttpPost("/admin/initpasswordreset")]
    public async Task<IActionResult> InitPasswordReset(InitPasswordResetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Require the user to have a confirmed email before they can log on.
        var user = await _userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
            return View(model);
        }

        var result = await _userManager.GeneratePasswordResetTokenAsync(user);
        model.PasswordResetToken = result;
        return View(model);
    }
}
