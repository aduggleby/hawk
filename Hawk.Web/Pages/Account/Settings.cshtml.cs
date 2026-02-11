using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hawk.Web.Pages.Account;

public sealed class SettingsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Identity/Account/Manage/Settings");
    public IActionResult OnPost() => Redirect("/Identity/Account/Manage/Settings");
}
