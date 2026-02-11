using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class IndexModel(UserManager<IdentityUser> userManager) : PageModel
{
    public string Username { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public bool EmailConfirmed { get; private set; }
    public bool HasPassword { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return NotFound($"Unable to load user with ID '{userManager.GetUserId(User)}'.");

        Username = user.UserName ?? string.Empty;
        Email = user.Email ?? string.Empty;
        EmailConfirmed = user.EmailConfirmed;
        HasPassword = await userManager.HasPasswordAsync(user);
        return Page();
    }
}

