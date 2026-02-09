// <file>
// <summary>
// Admin user creation page.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Admin.Users;

/// <summary>
/// Create user page model.
/// </summary>
public class CreateModel(UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Bound form data.
    /// </summary>
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>
    /// Creates a user.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return Page();

        var email = Input.Email.Trim();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var res = await userManager.CreateAsync(user, Input.Password);
        if (!res.Succeeded)
        {
            foreach (var e in res.Errors)
                ModelState.AddModelError(string.Empty, e.Description);
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
        /// Email/username.
        /// </summary>
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Initial password.
        /// </summary>
        [Required, MinLength(12)]
        public string Password { get; set; } = string.Empty;
    }
}

