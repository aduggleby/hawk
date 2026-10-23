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
public class EditModel(ApplicationDbContext db, IHostEnvironment env) : PageModel
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

        var m = await db.Monitors.FirstOrDefaultAsync(x => x.Id == Id, cancellationToken);
        if (m is null)
            return NotFound();

        // The type is immutable: validate and save against the persisted type, never the submitted one.
        IsHeartbeat = m.Type == MonitorType.Heartbeat;
        Form.Type = m.Type;

        MonitorFormValidation.AddResults(ModelState, Form.Validate(env));

        if (!ModelState.IsValid)
            return Page();

        if (!TryParseConcurrencyStamp(Form.ConcurrencyStamp, out var originalStamp))
        {
            TempData["FlashError"] = "This monitor changed while you were editing. Please reload and try again.";
            return RedirectToPage("/Monitors/Edit", new { id = Id });
        }

        // Ensure EF checks the stamp from when the user loaded the page, not the one we just fetched,
        // and renew it so other open Edit forms conflict with this change.
        db.Entry(m).Property(x => x.ConcurrencyStamp).OriginalValue = originalStamp;
        m.ConcurrencyStamp = Guid.NewGuid();

        var wasEnabled = m.Enabled;
        var oldPeriod = m.IntervalSeconds;
        var oldGrace = m.HeartbeatGraceSeconds;

        m.Name = Form.Name.Trim();
        m.Enabled = Form.Enabled;
        m.IntervalSeconds = Form.IntervalSeconds;
        m.AlertAfterConsecutiveFailures = Form.AlertAfterConsecutiveFailures;
        m.AlertEmailOverride = string.IsNullOrWhiteSpace(Form.AlertEmailOverride) ? null : Form.AlertEmailOverride.Trim();
        m.RunRetentionDays = Form.RunRetentionDays;

        if (IsHeartbeat)
        {
            m.HeartbeatGraceSeconds = Form.GraceSeconds;

            // NextRunAt is the ping deadline: only an intentional change (period, grace, re-enable) gives a fresh
            // period plus grace. Saving other fields must not move it.
            if (m.IntervalSeconds != oldPeriod || m.HeartbeatGraceSeconds != oldGrace || (!wasEnabled && m.Enabled) || m.NextRunAt is null)
                m.NextRunAt = HeartbeatTokens.InitialDeadline(DateTimeOffset.UtcNow, m.IntervalSeconds, m.HeartbeatGraceSeconds);
        }
        else
        {
            m.Url = (Form.Url ?? string.Empty).Trim();
            m.Method = Form.Method.Trim().ToUpperInvariant();
            m.TimeoutSeconds = Form.TimeoutSeconds;
            m.AllowedStatusCodes = AllowedStatusCodesParser.Normalize(Form.AllowedStatusCodes);
            m.ContentType = string.IsNullOrWhiteSpace(Form.ContentType) ? null : Form.ContentType.Trim();
            m.Body = Form.Body;

            // Reset scheduling to "run soon" when config changes.
            m.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(m.IntervalSeconds, 5, 24 * 60 * 60));
        }

        var newHeaders = new List<MonitorHeader>();
        for (var i = 0; !IsHeartbeat && i < Math.Min(Form.HeaderNames.Length, Form.HeaderValues.Length); i++)
        {
            var hn = (Form.HeaderNames[i] ?? string.Empty).Trim();
            var hv = (Form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn))
                continue;
            newHeaders.Add(new MonitorHeader { MonitorId = m.Id, Name = hn, Value = hv });
        }

        var newRules = new List<MonitorMatchRule>();
        for (var i = 0; !IsHeartbeat && i < Math.Min(Form.MatchModes.Length, Form.MatchPatterns.Length); i++)
        {
            var pat = (Form.MatchPatterns[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pat))
                continue;
            var mode = Form.MatchModes[i];
            if (mode == ContentMatchMode.None)
                continue;
            newRules.Add(new MonitorMatchRule { MonitorId = m.Id, Mode = mode, Pattern = pat });
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Replace children with set-based deletes to avoid per-row optimistic concurrency exceptions.
            await db.MonitorHeaders.Where(h => h.MonitorId == m.Id).ExecuteDeleteAsync(cancellationToken);
            await db.MonitorMatchRules.Where(r => r.MonitorId == m.Id).ExecuteDeleteAsync(cancellationToken);

            if (newHeaders.Count != 0)
                db.MonitorHeaders.AddRange(newHeaders);
            if (newRules.Count != 0)
                db.MonitorMatchRules.AddRange(newRules);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return RedirectToPage("/Monitors/Details", new { id = m.Id });
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request updated/deleted this monitor between when the user loaded the page and now.
            TempData["FlashError"] = "This monitor was changed by someone else while you were editing. Please review the latest values and try again.";
            return RedirectToPage("/Monitors/Edit", new { id = Id });
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
