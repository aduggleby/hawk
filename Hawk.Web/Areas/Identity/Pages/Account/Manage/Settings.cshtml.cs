using System.ComponentModel.DataAnnotations;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.UrlChecks;
using Hawk.Web.Services.UrlChecks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Areas.Identity.Pages.Account.Manage;

[Authorize]
public sealed class SettingsModel(ApplicationDbContext db, UserManager<IdentityUser> userManager, IConfiguration config) : PageModel
{
    [BindProperty]
    [Display(Name = "Alert email (account-wide override)")]
    [EmailAddress]
    [MaxLength(320)]
    public string? AlertEmail { get; set; }

    [BindProperty]
    [Display(Name = "Crawler User-Agent (account-wide override)")]
    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public IReadOnlyList<string> PresetKeys { get; private set; } = [];

    private void LoadPresets()
    {
        var keys = UrlCheckHttpOptions.BuiltInUserAgentPresets.Keys
            .Concat(config.GetSection("Hawk:UrlChecks:UserAgentPresets").GetChildren().Select(c => c.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PresetKeys = keys;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        LoadPresets();

        AlertEmail = await db.UserAlertSettings
            .Where(x => x.UserId == userId)
            .Select(x => x.AlertEmail)
            .FirstOrDefaultAsync(cancellationToken);

        UserAgent = await db.UserUrlCheckSettings
            .Where(x => x.UserId == userId)
            .Select(x => x.UserAgent)
            .FirstOrDefaultAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostAlertingAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        LoadPresets();

        if (!ModelState.IsValid)
            return Page();

        var email = string.IsNullOrWhiteSpace(AlertEmail) ? null : AlertEmail.Trim();
        var existing = await db.UserAlertSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (string.IsNullOrWhiteSpace(email))
        {
            if (existing is not null)
            {
                db.UserAlertSettings.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }
            TempData["FlashInfo"] = "Alert email override cleared.";
            return RedirectToPage();
        }

        if (existing is null)
        {
            db.UserAlertSettings.Add(new UserAlertSettings
            {
                UserId = userId,
                AlertEmail = email,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.AlertEmail = email;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        TempData["FlashInfo"] = "Alert email override saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCrawlerAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        LoadPresets();

        if (!ModelState.IsValid)
            return Page();

        var ua = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim();
        var existing = await db.UserUrlCheckSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);

        if (string.IsNullOrWhiteSpace(ua))
        {
            if (existing is not null)
            {
                db.UserUrlCheckSettings.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }
            TempData["FlashInfo"] = "Crawler User-Agent override cleared.";
            return RedirectToPage();
        }

        if (existing is null)
        {
            db.UserUrlCheckSettings.Add(new UserUrlCheckSettings
            {
                UserId = userId,
                UserAgent = ua,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.UserAgent = ua;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        TempData["FlashInfo"] = "Crawler User-Agent override saved.";
        return RedirectToPage();
    }
}

