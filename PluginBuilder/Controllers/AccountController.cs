using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Constants;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Controllers
{
    [Authorize]
    public class AccountController : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }
        private readonly PgpKeyService _pgpKeyService;

        public AccountController(
            DBConnectionFactory connectionFactory,
            PgpKeyService pgpKeyService,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            _pgpKeyService = pgpKeyService;
            UserManager = userManager;
        }

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetAccountDetailSettings(UserManager.GetUserId(User)!);
            return View(settings);
        }


        [HttpPost("details")]
        public async Task<IActionResult> AccountDetails(AccountSettings model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }
            await using var conn = await ConnectionFactory.Open();
            var user = UserManager.GetUserId(User)!;
            var accountSettings = await conn.GetAccountDetailSettings(user) ?? model;
            accountSettings.Nostr = model.Nostr ?? accountSettings.Nostr;
            accountSettings.Twitter = model.Twitter ?? accountSettings.Twitter;
            accountSettings.Github = model.Github ?? accountSettings.Github;
            accountSettings.Email = model.Email ?? accountSettings.Email;

            await conn.SetAccountDetailSettings(accountSettings, user);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }

        [HttpPost("saveaccountkeys")]
        public async Task<IActionResult> SaveAccountPgpKeys(AccountKeySettingsViewModel model)
        {
            try
            {
                await _pgpKeyService.AddNewPGGKeyAsync(model.PublicKey, model.Title, UserManager.GetUserId(User));
                TempData[WellKnownTempData.SuccessMessage] = "Account key added successfully";
                return RedirectToAction("AccountKeySettings");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", ex.Message);
                return RedirectToAction("AccountKeySettings");
            }
        }


        [HttpGet("accountkeysettings")]
        public async Task<IActionResult> AccountKeySettings()
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);
            var accountSettings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();

            var pgpKeyViewModels = accountSettings?.PgpKeys?
            .GroupBy(k => k.KeyBatchId)
            .Select(g => new PgpKeyViewModel
            {
                BatchId = g.FirstOrDefault()?.KeyBatchId,
                Title = g.FirstOrDefault()?.Title,
                KeyUserId = g.FirstOrDefault()?.KeyUserId,
                KeyId = g.FirstOrDefault(k => k.IsMasterKey)?.KeyId,
                Subkeys = string.Join(", ", g.Where(k => !k.IsMasterKey).Select(k => k.KeyId)),
                AddedDate = g.FirstOrDefault()?.AddedDate
            })
            .ToList();
            return View(pgpKeyViewModels);
        }

        [HttpPost("deleteaccountkey/{batchId}")]
        public async Task<IActionResult> DeleteAccountPgpKey(string batchId)
        {
            await using var conn = await ConnectionFactory.Open();
            string userId = UserManager.GetUserId(User);
            var accountSettings = await conn.GetAccountDetailSettings(userId);
            if (accountSettings == null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Account settings not found";
                return RedirectToAction("AccountKeySettings");
            }
            int removedCount = accountSettings.PgpKeys?.RemoveAll(k => k.KeyBatchId == batchId) ?? 0;
            if (removedCount == 0)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Invalid batch key";
                return RedirectToAction("AccountKeySettings");
            }
            await conn.SetAccountDetailSettings(accountSettings, userId);
            TempData[WellKnownTempData.SuccessMessage] = "Account key deleted successfully";
            return RedirectToAction("AccountKeySettings");
        }
    }
}
