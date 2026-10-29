// <file>
// <summary>
// Monitor run diagnostics page model.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Pages.Monitors.Runs;

/// <summary>
/// Displays full diagnostics for a single monitor run.
/// </summary>
public sealed class DetailsModel(ApplicationDbContext db) : PageModel
{
    /// <summary>
    /// Parent monitor.
    /// </summary>
    public MonitorEntity? Monitor { get; private set; }

    /// <summary>
    /// Selected run.
    /// </summary>
    public MonitorRun? Run { get; private set; }

    /// <summary>
    /// Queued alert email of this run (grouped alert emails), if any.
    /// </summary>
    public PendingAlert? QueuedAlert { get; private set; }

    /// <summary>
    /// Alert status line: queued / sent (alone or in a summary) / failed, or the run's own alert fields.
    /// </summary>
    public string AlertStatus => QueuedAlert switch
    {
        { SentAt: { } sentAt, SentInBatchOf: > 1 } => $"sent {sentAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC in a summary of {QueuedAlert.SentInBatchOf} alerts",
        { SentAt: { } sentAt } => $"sent {sentAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC",
        { FailedAt: not null } => $"failed: {QueuedAlert.LastError}",
        { LastError: { } error } => $"queued (send failed {QueuedAlert.Attempts}x, retrying: {error})",
        not null => "queued for a grouped email",
        null when Run is null => "-",
        null => Run.AlertSent ? "sent" : (Run.AlertError ?? "not sent"),
    };

    /// <summary>
    /// Formats raw JSON into indented JSON for diagnostics display.
    /// </summary>
    public string FormatJson(string? json, string fallback = "{}")
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    /// Loads monitor and run diagnostics.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(Guid monitorId, Guid runId, CancellationToken cancellationToken)
    {
        Monitor = await db.Monitors
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == monitorId, cancellationToken);
        if (Monitor is null)
            return NotFound();

        Run = await db.MonitorRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.MonitorId == monitorId, cancellationToken);
        if (Run is null)
            return NotFound();

        QueuedAlert = await db.PendingAlerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.RunId == runId, cancellationToken);

        return Page();
    }
}
