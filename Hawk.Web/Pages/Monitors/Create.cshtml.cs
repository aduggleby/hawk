// <file>
// <summary>
// Creates a new monitor.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Create monitor page model.
/// </summary>
public class CreateModel(ApplicationDbContext db, IHostEnvironment env, UserManager<IdentityUser> userManager) : PageModel
{
    /// <summary>
    /// Bound form data.
    /// </summary>
    [BindProperty]
    public MonitorForm Form { get; set; } = new();

    /// <summary>
    /// Allowed interval seconds.
    /// </summary>
    public IReadOnlyList<int> AllowedIntervals { get; private set; } = [];

    /// <summary>
    /// Loads the create form.
    /// </summary>
    public void OnGet()
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);
        Form.IntervalSeconds = AllowedIntervals.FirstOrDefault();
    }

    /// <summary>
    /// Creates a new monitor from the bound form.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        foreach (var vr in Form.Validate(env))
            ModelState.AddModelError(string.Join(".", vr.MemberNames), vr.ErrorMessage ?? "Invalid value.");

        if (!ModelState.IsValid)
            return Page();

        var m = new MonitorEntity
        {
            Name = Form.Name.Trim(),
            Url = Form.Url.Trim(),
            Method = Form.Method.Trim().ToUpperInvariant(),
            Enabled = Form.Enabled,
            TimeoutSeconds = Form.TimeoutSeconds,
            IntervalSeconds = Form.IntervalSeconds,
            AlertAfterConsecutiveFailures = Form.AlertAfterConsecutiveFailures,
            AlertEmailOverride = string.IsNullOrWhiteSpace(Form.AlertEmailOverride) ? null : Form.AlertEmailOverride.Trim(),
            AllowedStatusCodes = AllowedStatusCodesParser.Normalize(Form.AllowedStatusCodes),
            RunRetentionDays = Form.RunRetentionDays,
            ContentType = string.IsNullOrWhiteSpace(Form.ContentType) ? null : Form.ContentType.Trim(),
            Body = Form.Body,
            // Branch: can be null if authentication is disabled/misconfigured.
            CreatedByUserId = userManager.GetUserId(User),
        };

        for (var i = 0; i < Math.Min(Form.HeaderNames.Length, Form.HeaderValues.Length); i++)
        {
            var hn = (Form.HeaderNames[i] ?? string.Empty).Trim();
            var hv = (Form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn))
                continue;
            m.Headers.Add(new MonitorHeader { Name = hn, Value = hv });
        }

        for (var i = 0; i < Math.Min(Form.MatchModes.Length, Form.MatchPatterns.Length); i++)
        {
            var pat = (Form.MatchPatterns[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pat))
                continue;
            var mode = Form.MatchModes[i];
            if (mode == ContentMatchMode.None)
                continue;
            m.MatchRules.Add(new MonitorMatchRule { Mode = mode, Pattern = pat });
        }

        db.Monitors.Add(m);
        await db.SaveChangesAsync(cancellationToken);

        return RedirectToPage("/Monitors/Details", new { id = m.Id });
    }
}
