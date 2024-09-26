using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ViewModels;

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
                Email = user.Email!,
                UserName = user.UserName!,
                EmailConfirmed = user.EmailConfirmed,
                Roles = userRoles
            });
        }

        return View(model);
    }
}
