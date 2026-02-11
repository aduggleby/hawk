// <file>
// <summary>
// Admin edit user page for name, email, and roles.
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Admin.Users;

/// <summary>
/// Edit user page model.
/// </summary>
public class EditModel(
    UserManager<IdentityUser> userManager,
    RoleManager<IdentityRole> roleManager) : PageModel
{
    /// <summary>
    /// User id.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Bound form.
    /// </summary>
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>
    /// Available role choices.
    /// </summary>
    public List<string> AvailableRoles { get; private set; } = [];

    /// <summary>
    /// Selected role names.
    /// </summary>
    [BindProperty]
    public List<string> SelectedRoles { get; set; } = [];

    /// <summary>
    /// Loads user and role selections.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id);
        if (user is null)
            return NotFound();

        LoadRoles();
        Input = new InputModel
        {
            Name = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
        };
        SelectedRoles = (await userManager.GetRolesAsync(user)).OrderBy(x => x).ToList();
        return Page();
    }

    /// <summary>
    /// Saves user updates and role assignments.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id);
        if (user is null)
            return NotFound();

        LoadRoles();
        SelectedRoles = SelectedRoles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var role in SelectedRoles)
        {
            if (!AvailableRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                ModelState.AddModelError(nameof(SelectedRoles), $"Unknown role '{role}'.");
        }

        if (!ModelState.IsValid)
            return Page();

        if (!string.Equals(user.UserName, Input.Name, StringComparison.Ordinal))
        {
            var setNameRes = await userManager.SetUserNameAsync(user, Input.Name.Trim());
            if (!setNameRes.Succeeded)
            {
                AddErrors(setNameRes);
                return Page();
            }
        }

        if (!string.Equals(user.Email, Input.Email, StringComparison.OrdinalIgnoreCase))
        {
            var setEmailRes = await userManager.SetEmailAsync(user, Input.Email.Trim());
            if (!setEmailRes.Succeeded)
            {
                AddErrors(setEmailRes);
                return Page();
            }
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        var toAdd = SelectedRoles.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToArray();
        var toRemove = currentRoles.Except(SelectedRoles, StringComparer.OrdinalIgnoreCase).ToArray();

        if (toRemove.Length != 0)
        {
            var removeRes = await userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removeRes.Succeeded)
            {
                AddErrors(removeRes);
                return Page();
            }
        }

        if (toAdd.Length != 0)
        {
            var addRes = await userManager.AddToRolesAsync(user, toAdd);
            if (!addRes.Succeeded)
            {
                AddErrors(addRes);
                return Page();
            }
        }

        TempData["FlashInfo"] = "User updated.";
        return RedirectToPage("/Admin/Users/Index");
    }

    private void LoadRoles()
    {
        AvailableRoles = roleManager.Roles
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n)
            .Select(n => n!)
            .ToList();
    }

    private void AddErrors(IdentityResult result)
    {
        foreach (var e in result.Errors)
            ModelState.AddModelError(string.Empty, e.Description);
    }

    /// <summary>
    /// Edit form.
    /// </summary>
    public sealed class InputModel
    {
        /// <summary>
        /// User name.
        /// </summary>
        [Required]
        [StringLength(256)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// User email.
        /// </summary>
        [Required]
        [EmailAddress]
        [StringLength(256)]
        public string Email { get; set; } = string.Empty;
    }
}
