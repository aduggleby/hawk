using System.ComponentModel.DataAnnotations;
using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
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

    [BindProperty]
    [Display(Name = "Run retention (days, account-wide override)")]
    [Range(1, 3650)]
    public int? RunRetentionDays { get; set; }

    public IReadOnlyList<string> PresetKeys { get; private set; } = [];

    public string ServerDefaultUserAgent { get; private set; } = "firefox";

    private void LoadPresets()
    {
        var keys = UrlCheckHttpOptions.BuiltInUserAgentPresets.Keys
            .Concat(config.GetSection("Hawk:UrlChecks:UserAgentPresets").GetChildren().Select(c => c.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PresetKeys = keys;
        ServerDefaultUserAgent = (config["Hawk:UrlChecks:UserAgent"] ?? "firefox").Trim();
        if (string.IsNullOrWhiteSpace(ServerDefaultUserAgent))
            ServerDefaultUserAgent = "firefox";
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

        RunRetentionDays = await db.UserMonitorSettings
            .Where(x => x.UserId == userId)
            .Select(x => x.RunRetentionDays)
            .FirstOrDefaultAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        LoadPresets();

        if (!ModelState.IsValid)
            return Page();

        var email = string.IsNullOrWhiteSpace(AlertEmail) ? null : AlertEmail.Trim();
        var ua = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim();

        var alertSettings = await db.UserAlertSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (email is null)
        {
            if (alertSettings is not null)
                db.UserAlertSettings.Remove(alertSettings);
        }
        else if (alertSettings is null)
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
            alertSettings.AlertEmail = email;
            alertSettings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var crawlerSettings = await db.UserUrlCheckSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (ua is null)
        {
            if (crawlerSettings is not null)
                db.UserUrlCheckSettings.Remove(crawlerSettings);
        }
        else if (crawlerSettings is null)
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
            crawlerSettings.UserAgent = ua;
            crawlerSettings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var monitorSettings = await db.UserMonitorSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (RunRetentionDays is null)
        {
            if (monitorSettings is not null)
                db.UserMonitorSettings.Remove(monitorSettings);
        }
        else if (monitorSettings is null)
        {
            db.UserMonitorSettings.Add(new UserMonitorSettings
            {
                UserId = userId,
                RunRetentionDays = RunRetentionDays,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            monitorSettings.RunRetentionDays = RunRetentionDays;
            monitorSettings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        TempData["FlashInfo"] = "Settings saved.";
        return RedirectToPage();
    }
}
