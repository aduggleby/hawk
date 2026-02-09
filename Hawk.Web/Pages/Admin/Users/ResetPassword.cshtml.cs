// <file>
// <summary>
// Admin reset password page.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Admin.Users;

/// <summary>
/// Reset password page model.
/// </summary>
public class ResetPasswordModel(UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// User id.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display email.
    /// </summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>
    /// Bound form data.
    /// </summary>
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>
    /// Loads user.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id);
        if (user is null)
            return NotFound();
        Email = user.Email ?? user.UserName ?? user.Id;
        return Page();
    }

    /// <summary>
    /// Resets password.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return await OnGetAsync(cancellationToken);

        var user = await userManager.FindByIdAsync(Id);
        if (user is null)
            return NotFound();

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var res = await userManager.ResetPasswordAsync(user, token, Input.NewPassword);
        if (!res.Succeeded)
        {
            foreach (var e in res.Errors)
                ModelState.AddModelError(string.Empty, e.Description);
            Email = user.Email ?? user.UserName ?? user.Id;
            return Page();
        }

        return RedirectToPage("/Admin/Users/Index");
    }

    /// <summary>
    /// Form input model.
    /// </summary>
    public sealed class InputModel
    {
        /// <summary>
        /// New password.
        /// </summary>
        [Required, MinLength(12)]
        public string NewPassword { get; set; } = string.Empty;
    }
}

