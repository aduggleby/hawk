using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class ChangePasswordModel(
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    ILogger<ChangePasswordModel> logger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string OldPassword { get; set; } = string.Empty;

        [Required]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 8)]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound($"Unable to load user with ID '{userManager.GetUserId(User)}'.");

        if (!await userManager.HasPasswordAsync(user))
            return RedirectToPage("./Index");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound($"Unable to load user with ID '{userManager.GetUserId(User)}'.");

        var changePasswordResult = await userManager.ChangePasswordAsync(user, Input.OldPassword, Input.NewPassword);
        if (!changePasswordResult.Succeeded)
        {
            foreach (var error in changePasswordResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await signInManager.RefreshSignInAsync(user);
        logger.LogInformation("User changed their password successfully.");
        TempData["FlashInfo"] = "Password changed successfully.";
        return RedirectToPage();
    }
}

