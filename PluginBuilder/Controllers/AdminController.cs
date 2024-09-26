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
        var users = _userManager.Users.ToList(); // Retrieve all users

        // Create a list of UserRoleViewModel to store user details with roles
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

    // POST: Update User Roles
    [HttpGet("editroles")]
    public async Task<IActionResult> EditRoles(string userId, List<string> selectedRoles)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var rolesToAdd = selectedRoles.Except(currentRoles).ToList();
        var rolesToRemove = currentRoles.Except(selectedRoles).ToList();

        // Add new roles
        await _userManager.AddToRolesAsync(user, rolesToAdd);

        // Remove unselected roles
        await _userManager.RemoveFromRolesAsync(user, rolesToRemove);

        return RedirectToAction("Users");
    }
}
