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
            Name = m.Name,
            Url = m.Url,
            Method = m.Method,
            Enabled = m.Enabled,
            TimeoutSeconds = m.TimeoutSeconds,
            IntervalSeconds = AllowedIntervals.Contains(m.IntervalSeconds) ? m.IntervalSeconds : AllowedIntervals.FirstOrDefault(),
            AlertAfterConsecutiveFailures = Math.Clamp(m.AlertAfterConsecutiveFailures, 1, 20),
            AlertEmailOverride = m.AlertEmailOverride,
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

    /// <summary>
    /// Applies edits.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        AllowedIntervals = Services.Monitoring.MonitorIntervals.AllowedSeconds(env);

        foreach (var vr in Form.Validate(env))
            ModelState.AddModelError(string.Join(".", vr.MemberNames), vr.ErrorMessage ?? "Invalid value.");

        if (!ModelState.IsValid)
            return Page();

        var m = await db.Monitors
            .Include(x => x.Headers)
            .Include(x => x.MatchRules)
            .FirstOrDefaultAsync(x => x.Id == Id, cancellationToken);
        if (m is null)
            return NotFound();

        m.Name = Form.Name.Trim();
        m.Url = Form.Url.Trim();
        m.Method = Form.Method.Trim().ToUpperInvariant();
        m.Enabled = Form.Enabled;
        m.TimeoutSeconds = Form.TimeoutSeconds;
        m.IntervalSeconds = Form.IntervalSeconds;
        m.AlertAfterConsecutiveFailures = Form.AlertAfterConsecutiveFailures;
        m.AlertEmailOverride = string.IsNullOrWhiteSpace(Form.AlertEmailOverride) ? null : Form.AlertEmailOverride.Trim();
        m.ContentType = string.IsNullOrWhiteSpace(Form.ContentType) ? null : Form.ContentType.Trim();
        m.Body = Form.Body;

        // Branch: simplest edit behavior is replace child collections.
        m.Headers.Clear();
        for (var i = 0; i < Math.Min(Form.HeaderNames.Length, Form.HeaderValues.Length); i++)
        {
            var hn = (Form.HeaderNames[i] ?? string.Empty).Trim();
            var hv = (Form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn))
                continue;
            m.Headers.Add(new MonitorHeader { Name = hn, Value = hv });
        }

        m.MatchRules.Clear();
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

        // Reset scheduling to "run soon" when config changes.
        m.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(m.IntervalSeconds, 5, 24 * 60 * 60));

        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("/Monitors/Details", new { id = m.Id });
    }
}
