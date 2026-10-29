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
using Microsoft.AspNetCore.Mvc.Filters;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Web.Pages.Monitors;

/// <summary>
/// Create monitor page model.
/// </summary>
public class CreateModel(ApplicationDbContext db, IHostEnvironment env, UserManager<IdentityUser> userManager, MonitorEditor editor) : PageModel
{
    /// <summary>
    /// Bound form data.
    /// </summary>
    [BindProperty]
    public MonitorForm Form { get; set; } = new();

    [BindProperty]
    public IFormFile? ImportFile { get; set; }

    /// <summary>
    /// Existing group names for the group field's suggestions.
    /// </summary>
    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    /// <inheritdoc />
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        GroupNames = await MonitorGroups.ListAsync(db, context.HttpContext.RequestAborted);
        await next();
    }

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
            Group = MonitorGroups.Normalize(model.Group),
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
            FollowRedirects = model.FollowRedirects ?? true,
            RunRetentionDays = model.RunRetentionDays,
            ContentType = model.ContentType,
            Body = model.Body,
        };

        // Flatten headers/match rules into the form arrays (max 5).
        for (var i = 0; i < Math.Min(MonitorForm.MaxHeaders, model.Headers.Count); i++)
        {
            Form.HeaderNames[i] = model.Headers[i].Name ?? string.Empty;
            Form.HeaderValues[i] = model.Headers[i].Value ?? string.Empty;
        }

        for (var i = 0; i < Math.Min(MonitorForm.MaxMatchRules, model.MatchRules.Count); i++)
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

        var m = await editor.CreateAsync(Form, userManager.GetUserId(User), cancellationToken);

        return RedirectToPage("/Monitors/Details", new { id = m.Id });
    }
}
