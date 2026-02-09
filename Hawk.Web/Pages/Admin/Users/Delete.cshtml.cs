// <file>
// <summary>
// Admin delete user confirmation.
// </summary>
// </file>

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Admin.Users;

/// <summary>
/// Delete user page model.
/// </summary>
public class DeleteModel(UserManager<IdentityUser> userManager) : PageModel
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
    /// Deletes user.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id);
        if (user is null)
            return NotFound();

        var res = await userManager.DeleteAsync(user);
        if (!res.Succeeded)
        {
            foreach (var e in res.Errors)
                ModelState.AddModelError(string.Empty, e.Description);
            Email = user.Email ?? user.UserName ?? user.Id;
            return Page();
        }

        return RedirectToPage("/Admin/Users/Index");
    }
}

