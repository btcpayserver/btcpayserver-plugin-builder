using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;

namespace PluginBuilder.Controllers;

[Authorize(Roles = "ServerAdmin")]
public class AdminController : Controller
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public AdminController(UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
    }

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
            UserId = user.Id,
            UserName = user.UserName,
            UserRoles = userRoles,
            AvailableRoles = allRoles
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
                    UserId = userId,
                    UserRoles = currentRoles.ToList(),
                    AvailableRoles = _roleManager.Roles.ToList()
                };

                return View(model);
            }
        }

        await _userManager.AddToRolesAsync(user, rolesToAdd);
        await _userManager.RemoveFromRolesAsync(user, rolesToRemove);

        return RedirectToAction("Users");
    }
}
