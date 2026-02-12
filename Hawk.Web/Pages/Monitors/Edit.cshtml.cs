// <file>
// <summary>
// Edits an existing monitor.
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

        Form = new MonitorForm
        {
            RowVersion = Convert.ToBase64String(m.RowVersion ?? []),
            Name = m.Name,
            Url = m.Url,
            Method = m.Method,
            Enabled = m.Enabled,
            TimeoutSeconds = m.TimeoutSeconds,
            IntervalSeconds = AllowedIntervals.Contains(m.IntervalSeconds) ? m.IntervalSeconds : AllowedIntervals.FirstOrDefault(),
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

    private static bool TryParseRowVersion(string? rowVersionBase64, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrWhiteSpace(rowVersionBase64))
            return false;

        try
        {
            rowVersion = Convert.FromBase64String(rowVersionBase64);
            return rowVersion.Length != 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies edits.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        MonitorFormValidation.AddResults(ModelState, Form.Validate(env));

        if (!ModelState.IsValid)
            return Page();

        if (!TryParseRowVersion(Form.RowVersion, out var originalRowVersion))
        {
            TempData["FlashError"] = "This monitor changed while you were editing. Please reload and try again.";
            return RedirectToPage("/Monitors/Edit", new { id = Id });
        }

        var m = await db.Monitors.FirstOrDefaultAsync(x => x.Id == Id, cancellationToken);
        if (m is null)
            return NotFound();

        // Ensure EF uses the rowversion from when the user loaded the page, not the one we just fetched.
        db.Entry(m).Property(x => x.RowVersion).OriginalValue = originalRowVersion;

        m.Name = Form.Name.Trim();
        m.Url = Form.Url.Trim();
        m.Method = Form.Method.Trim().ToUpperInvariant();
        m.Enabled = Form.Enabled;
        m.TimeoutSeconds = Form.TimeoutSeconds;
        m.IntervalSeconds = Form.IntervalSeconds;
        m.AlertAfterConsecutiveFailures = Form.AlertAfterConsecutiveFailures;
        m.AlertEmailOverride = string.IsNullOrWhiteSpace(Form.AlertEmailOverride) ? null : Form.AlertEmailOverride.Trim();
        m.AllowedStatusCodes = AllowedStatusCodesParser.Normalize(Form.AllowedStatusCodes);
        m.RunRetentionDays = Form.RunRetentionDays;
        m.ContentType = string.IsNullOrWhiteSpace(Form.ContentType) ? null : Form.ContentType.Trim();
        m.Body = Form.Body;

        // Reset scheduling to "run soon" when config changes.
        m.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(m.IntervalSeconds, 5, 24 * 60 * 60));

        var newHeaders = new List<MonitorHeader>();
        for (var i = 0; i < Math.Min(Form.HeaderNames.Length, Form.HeaderValues.Length); i++)
        {
            var hn = (Form.HeaderNames[i] ?? string.Empty).Trim();
            var hv = (Form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn))
                continue;
            newHeaders.Add(new MonitorHeader { MonitorId = m.Id, Name = hn, Value = hv });
        }

        var newRules = new List<MonitorMatchRule>();
        for (var i = 0; i < Math.Min(Form.MatchModes.Length, Form.MatchPatterns.Length); i++)
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
}
