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

    [BindProperty]
    public IFormFile? ImportFile { get; set; }

    /// <summary>
    /// Allowed interval seconds.
    /// </summary>
    public IReadOnlyList<int> AllowedIntervals { get; private set; } = [];

    /// <summary>
    /// Allowed heartbeat periods (seconds).
    /// </summary>
    public IReadOnlyList<int> AllowedHeartbeatPeriods => Services.Monitoring.MonitorIntervals.AllowedHeartbeatPeriods(env);

    /// <summary>
    /// Allowed heartbeat grace times (seconds).
    /// </summary>
    public IReadOnlyList<int> AllowedHeartbeatGraces => Services.Monitoring.MonitorIntervals.AllowedHeartbeatGraces(env);

    /// <summary>
    /// Loads the create form.
    /// </summary>
    public void OnGet()
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);
        Form.IntervalSeconds = AllowedIntervals.FirstOrDefault();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        if (ImportFile is null || ImportFile.Length <= 0)
        {
            TempData["FlashError"] = "Select a JSON file to import.";
            return RedirectToPage();
        }

        string json;
        await using (var stream = ImportFile.OpenReadStream())
        using (var reader = new StreamReader(stream))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        if (!MonitorJsonPort.TryParse(json, out var envelope, out var parseError) || envelope is null)
        {
            TempData["FlashError"] = parseError ?? "Invalid monitor JSON.";
            return Page();
        }

        if (envelope.Monitors.Count == 0)
        {
            TempData["FlashError"] = "Import JSON does not contain any monitors.";
            return Page();
        }

        if (envelope.Monitors.Count > 1)
            TempData["FlashInfo"] = $"Import file contains {envelope.Monitors.Count} monitors. Loaded the first into the form.";

        var model = envelope.Monitors[0];

        var heartbeat = model.Type == MonitorType.Heartbeat;
        var allowedIntervals = heartbeat ? AllowedHeartbeatPeriods : AllowedIntervals;
        var interval = allowedIntervals.Contains(model.IntervalSeconds) ? model.IntervalSeconds : allowedIntervals.FirstOrDefault();

        // A heartbeat's exported token is not carried over: creating the form always issues a new one.
        Form = new MonitorForm
        {
            Name = model.Name ?? string.Empty,
            Type = model.Type,
            GraceSeconds = model.HeartbeatGraceSeconds,
            Url = model.Url ?? string.Empty,
            Method = string.IsNullOrWhiteSpace(model.Method) ? "GET" : model.Method,
            Enabled = model.Enabled,
            TimeoutSeconds = model.TimeoutSeconds,
            IntervalSeconds = interval,
            AlertAfterConsecutiveFailures = model.AlertAfterConsecutiveFailures,
            AlertEmailOverride = model.AlertEmailOverride,
            AllowedStatusCodes = model.AllowedStatusCodes,
            RunRetentionDays = model.RunRetentionDays,
            ContentType = model.ContentType,
            Body = model.Body,
        };

        // Flatten headers/match rules into the form arrays (max 5).
        for (var i = 0; i < Math.Min(5, model.Headers.Count); i++)
        {
            Form.HeaderNames[i] = model.Headers[i].Name ?? string.Empty;
            Form.HeaderValues[i] = model.Headers[i].Value ?? string.Empty;
        }

        for (var i = 0; i < Math.Min(5, model.MatchRules.Count); i++)
        {
            Form.MatchModes[i] = model.MatchRules[i].Mode;
            Form.MatchPatterns[i] = model.MatchRules[i].Pattern ?? string.Empty;
        }

        // Avoid showing stale validation errors from a previous post.
        ModelState.Clear();

        if (model.IsPaused)
            TempData["FlashInfo"] = "Imported monitor was paused. Pause state is not part of the create form.";
        else if (TempData["FlashInfo"] is null)
            TempData["FlashInfo"] = "Imported monitor JSON into the form. Review the values, then click Create.";

        return Page();
    }

    /// <summary>
    /// Creates a new monitor from the bound form.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        MonitorFormValidation.AddResults(ModelState, Form.Validate(env));

        if (!ModelState.IsValid)
            return Page();

        var heartbeat = Form.Type == MonitorType.Heartbeat;
        var m = new MonitorEntity
        {
            Name = Form.Name.Trim(),
            Type = Form.Type,
            Url = heartbeat ? string.Empty : (Form.Url ?? string.Empty).Trim(),
            Method = heartbeat ? "GET" : Form.Method.Trim().ToUpperInvariant(),
            Enabled = Form.Enabled,
            TimeoutSeconds = Form.TimeoutSeconds,
            IntervalSeconds = Form.IntervalSeconds,
            AlertAfterConsecutiveFailures = Form.AlertAfterConsecutiveFailures,
            AlertEmailOverride = string.IsNullOrWhiteSpace(Form.AlertEmailOverride) ? null : Form.AlertEmailOverride.Trim(),
            AllowedStatusCodes = heartbeat ? null : AllowedStatusCodesParser.Normalize(Form.AllowedStatusCodes),
            RunRetentionDays = Form.RunRetentionDays,
            ContentType = heartbeat || string.IsNullOrWhiteSpace(Form.ContentType) ? null : Form.ContentType.Trim(),
            Body = heartbeat ? null : Form.Body,
            // Branch: can be null if authentication is disabled/misconfigured.
            CreatedByUserId = userManager.GetUserId(User),
        };

        if (heartbeat)
        {
            // The scheduler never writes a heartbeat's NextRunAt: it is the deadline, so a new monitor gets a full period plus grace.
            m.HeartbeatGraceSeconds = Form.GraceSeconds;
            m.HeartbeatToken = HeartbeatTokens.Generate();
            m.NextRunAt = HeartbeatTokens.InitialDeadline(DateTimeOffset.UtcNow, m.IntervalSeconds, m.HeartbeatGraceSeconds);
        }

        for (var i = 0; !heartbeat && i < Math.Min(Form.HeaderNames.Length, Form.HeaderValues.Length); i++)
        {
            var hn = (Form.HeaderNames[i] ?? string.Empty).Trim();
            var hv = (Form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn))
                continue;
            m.Headers.Add(new MonitorHeader { Name = hn, Value = hv });
        }

        for (var i = 0; !heartbeat && i < Math.Min(Form.MatchModes.Length, Form.MatchPatterns.Length); i++)
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
