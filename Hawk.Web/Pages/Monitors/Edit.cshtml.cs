// <file>
// <summary>
// Edits an existing monitor.
// Optimistic concurrency: the monitor's ConcurrencyStamp round-trips through a hidden field. Only intentional
// user changes renew the stamp, so scheduler/executor bookkeeping (NextRunAt/LastRunAt) never causes a conflict.
// </summary>
// </file>

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Edit monitor page model.
/// </summary>
public class EditModel(ApplicationDbContext db, IHostEnvironment env, MonitorEditor editor) : PageModel
{
    /// <summary>
    /// Monitor id.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

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
    /// True when the persisted monitor is a heartbeat. The type is immutable and never taken from the form.
    /// </summary>
    public bool IsHeartbeat { get; private set; }

    /// <summary>
    /// Allowed heartbeat periods (seconds).
    /// </summary>
    public IReadOnlyList<int> AllowedHeartbeatPeriods => Services.Monitoring.MonitorIntervals.AllowedHeartbeatPeriods(env);

    /// <summary>
    /// Allowed heartbeat grace times (seconds).
    /// </summary>
    public IReadOnlyList<int> AllowedHeartbeatGraces => Services.Monitoring.MonitorIntervals.AllowedHeartbeatGraces(env);

    /// <summary>
    /// Loads the edit form.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        var m = await db.Monitors
            .Include(x => x.Headers)
            .Include(x => x.MatchRules)
            .FirstOrDefaultAsync(x => x.Id == Id, cancellationToken);
        if (m is null)
            return NotFound();

        IsHeartbeat = m.Type == MonitorType.Heartbeat;
        var intervals = IsHeartbeat ? AllowedHeartbeatPeriods : AllowedIntervals;

        Form = new MonitorForm
        {
            ConcurrencyStamp = m.ConcurrencyStamp.ToString("D"),
            Name = m.Name,
            Type = m.Type,
            Url = m.Url,
            Method = m.Method,
            Enabled = m.Enabled,
            TimeoutSeconds = m.TimeoutSeconds,
            IntervalSeconds = intervals.Contains(m.IntervalSeconds) ? m.IntervalSeconds : intervals.FirstOrDefault(),
            GraceSeconds = m.HeartbeatGraceSeconds,
            AlertAfterConsecutiveFailures = Math.Clamp(m.AlertAfterConsecutiveFailures, 1, 20),
            AlertEmailOverride = m.AlertEmailOverride,
            AllowedStatusCodes = m.AllowedStatusCodes,
            RunRetentionDays = m.RunRetentionDays,
            ContentType = m.ContentType,
            Body = m.Body,
        };

        for (var i = 0; i < Math.Min(5, m.Headers.Count); i++)
        {
            Form.HeaderNames[i] = m.Headers[i].Name;
            Form.HeaderValues[i] = m.Headers[i].Value;
        }

        for (var i = 0; i < Math.Min(5, m.MatchRules.Count); i++)
        {
            Form.MatchModes[i] = m.MatchRules[i].Mode;
            Form.MatchPatterns[i] = m.MatchRules[i].Pattern;
        }

        return Page();
    }

    private static bool TryParseConcurrencyStamp(string? value, out Guid stamp) =>
        Guid.TryParse(value, out stamp);

    /// <summary>
    /// Applies edits.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        var type = await db.Monitors.AsNoTracking()
            .Where(x => x.Id == Id)
            .Select(x => (MonitorType?)x.Type)
            .FirstOrDefaultAsync(cancellationToken);
        if (type is null)
            return NotFound();

        // The type is immutable: validate and save against the persisted type, never the submitted one.
        IsHeartbeat = type == MonitorType.Heartbeat;
        Form.Type = type.Value;

        MonitorFormValidation.AddResults(ModelState, Form.Validate(env));

        if (!ModelState.IsValid)
            return Page();

        if (!TryParseConcurrencyStamp(Form.ConcurrencyStamp, out var originalStamp))
        {
            TempData["FlashError"] = "This monitor changed while you were editing. Please reload and try again.";
            return RedirectToPage("/Monitors/Edit", new { id = Id });
        }

        // The editor checks the stamp from when the user loaded the page (not the one it fetches) and renews it.
        switch (await editor.UpdateAsync(Id, Form, originalStamp, replaceHeaders: true, replaceMatchRules: true, cancellationToken))
        {
            case MonitorUpdateOutcome.NotFound:
                return NotFound();
            case MonitorUpdateOutcome.Conflict:
                // Another request updated/deleted this monitor between when the user loaded the page and now.
                TempData["FlashError"] = "This monitor was changed by someone else while you were editing. Please review the latest values and try again.";
                return RedirectToPage("/Monitors/Edit", new { id = Id });
            default:
                return RedirectToPage("/Monitors/Details", new { id = Id });
        }
    }

    /// <summary>
    /// Issues a new ping token for a heartbeat; the old URL stops working immediately.
    /// </summary>
    public async Task<IActionResult> OnPostRegenerateAsync(CancellationToken cancellationToken)
    {
        // Set-based: independent of the Edit form's concurrency stamp. Still an intentional user change, so the stamp is renewed.
        var token = HeartbeatTokens.Generate();
        var stamp = Guid.NewGuid();
        var updated = await db.Monitors
            .Where(m => m.Id == Id && m.Type == MonitorType.Heartbeat)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.HeartbeatToken, token)
                .SetProperty(m => m.ConcurrencyStamp, stamp), cancellationToken);
        if (updated == 0)
            return NotFound();

        TempData["FlashInfo"] = "Issued a new ping URL. The old URL no longer works.";
        return RedirectToPage("/Monitors/Details", new { id = Id });
    }
}
