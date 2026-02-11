// <file>
// <summary>
// Admin user management: list users and basic operations.
// </summary>
// </file>

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Pages.Admin.Users;

/// <summary>
/// Lists application users.
/// </summary>
public class IndexModel(UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Users to render.
    /// </summary>
    public List<UserRow> Users { get; private set; } = [];

    /// <summary>
    /// Loads users and their roles.
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);

        var rows = new List<UserRow>(users.Count);
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            rows.Add(new UserRow(
                u.Id,
                u.UserName ?? u.Email ?? u.Id,
                u.Email ?? u.UserName ?? u.Id,
                roles.ToArray()));
        }
        Users = rows;
    }

    /// <summary>
    /// Row view model.
    /// </summary>
    public sealed record UserRow(string Id, string Name, string Email, string[] Roles);
}
