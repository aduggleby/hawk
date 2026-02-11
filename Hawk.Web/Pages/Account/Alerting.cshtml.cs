// <file>
// <summary>
// Per-user alerting settings page (alert recipient email override).
// </summary>
// </file>

using System.ComponentModel.DataAnnotations;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Pages.Account;

public sealed class AlertingModel : PageModel
{
    [BindProperty]
    [Display(Name = "Alert email (account-wide override)")]
    [EmailAddress]
    [MaxLength(320)]
    public string? AlertEmail { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return Redirect("/Identity/Account/Manage/Settings");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        return Redirect("/Identity/Account/Manage/Settings");
    }
}
