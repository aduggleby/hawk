// <file>
// <summary>
// Creates, updates and deletes monitors from an already validated MonitorForm. Shared by the Create/Edit/Delete pages and the agent API,
// so entity mapping, heartbeat deadline rules and concurrency-stamp handling exist once. Callers validate the form first.
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>Result of <see cref="MonitorEditor.UpdateAsync"/>.</summary>
public enum MonitorUpdateOutcome { Updated, NotFound, Conflict }

/// <summary>Result of <see cref="MonitorEditor.DeleteAsync"/>.</summary>
public enum MonitorDeleteOutcome { Deleted, NotFound, Conflict }

public sealed class MonitorEditor(ApplicationDbContext db)
{
    /// <summary>Attempts at an update without an expected stamp when a concurrent write wins the race (last write wins).</summary>
    private const int MaxUnconditionalAttempts = 3;

    /// <summary>
    /// Creates a monitor owned by <paramref name="ownerUserId"/>. A heartbeat gets a new token and a deadline of now + period + grace
    /// (the scheduler never writes a heartbeat's NextRunAt).
    /// </summary>
    public async Task<MonitorEntity> CreateAsync(MonitorForm form, string? ownerUserId, CancellationToken cancellationToken)
    {
        var heartbeat = form.Type == MonitorType.Heartbeat;
        var m = new MonitorEntity
        {
            Name = form.Name.Trim(),
            Type = form.Type,
            Url = heartbeat ? string.Empty : (form.Url ?? string.Empty).Trim(),
            Method = heartbeat ? "GET" : form.Method.Trim().ToUpperInvariant(),
            Enabled = form.Enabled,
            TimeoutSeconds = form.TimeoutSeconds,
            IntervalSeconds = form.IntervalSeconds,
            AlertAfterConsecutiveFailures = form.AlertAfterConsecutiveFailures,
            AlertEmailOverride = string.IsNullOrWhiteSpace(form.AlertEmailOverride) ? null : form.AlertEmailOverride.Trim(),
            AllowedStatusCodes = heartbeat ? null : AllowedStatusCodesParser.Normalize(form.AllowedStatusCodes),
            RunRetentionDays = form.RunRetentionDays,
            ContentType = heartbeat || string.IsNullOrWhiteSpace(form.ContentType) ? null : form.ContentType.Trim(),
            Body = heartbeat ? null : form.Body,
            // Branch: can be null if authentication is disabled/misconfigured.
            CreatedByUserId = ownerUserId,
        };

        if (heartbeat)
        {
            m.HeartbeatGraceSeconds = form.GraceSeconds;
            m.HeartbeatToken = HeartbeatTokens.Generate();
            m.NextRunAt = HeartbeatTokens.InitialDeadline(DateTimeOffset.UtcNow, m.IntervalSeconds, m.HeartbeatGraceSeconds);
        }
        else
        {
            foreach (var header in HeaderRows(form, m.Id))
                m.Headers.Add(header);
            foreach (var rule in RuleRows(form, m.Id))
                m.MatchRules.Add(rule);
        }

        db.Monitors.Add(m);
        await db.SaveChangesAsync(cancellationToken);
        return m;
    }

    /// <summary>
    /// Applies <paramref name="form"/> to the monitor. The persisted type wins over <c>form.Type</c> (the type is immutable), so callers must
    /// validate against the persisted type. With <paramref name="expectedStamp"/> the save conflicts when the stamp changed; without it the
    /// last write wins. Headers and match rules are replaced only when the matching flag is set. The stamp is always renewed.
    /// </summary>
    public async Task<MonitorUpdateOutcome> UpdateAsync(
        Guid id, MonitorForm form, Guid? expectedStamp, bool replaceHeaders, bool replaceMatchRules, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var m = await db.Monitors.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (m is null)
                return MonitorUpdateOutcome.NotFound;

            // EF checks the stamp the caller saw, not the one just fetched; renewing it makes other open forms conflict.
            if (expectedStamp is { } expected)
                db.Entry(m).Property(x => x.ConcurrencyStamp).OriginalValue = expected;
            m.ConcurrencyStamp = Guid.NewGuid();

            var heartbeat = m.Type == MonitorType.Heartbeat;
            var wasEnabled = m.Enabled;
            var oldPeriod = m.IntervalSeconds;
            var oldGrace = m.HeartbeatGraceSeconds;

            m.Name = form.Name.Trim();
            m.Enabled = form.Enabled;
            m.IntervalSeconds = form.IntervalSeconds;
            m.AlertAfterConsecutiveFailures = form.AlertAfterConsecutiveFailures;
            m.AlertEmailOverride = string.IsNullOrWhiteSpace(form.AlertEmailOverride) ? null : form.AlertEmailOverride.Trim();
            m.RunRetentionDays = form.RunRetentionDays;

            if (heartbeat)
            {
                m.HeartbeatGraceSeconds = form.GraceSeconds;

                // NextRunAt is the ping deadline: only an intentional change (period, grace, re-enable) gives a fresh
                // period plus grace. Saving other fields must not move it.
                if (m.IntervalSeconds != oldPeriod || m.HeartbeatGraceSeconds != oldGrace || (!wasEnabled && m.Enabled) || m.NextRunAt is null)
                    m.NextRunAt = HeartbeatTokens.InitialDeadline(DateTimeOffset.UtcNow, m.IntervalSeconds, m.HeartbeatGraceSeconds);
            }
            else
            {
                m.Url = (form.Url ?? string.Empty).Trim();
                m.Method = form.Method.Trim().ToUpperInvariant();
                m.TimeoutSeconds = form.TimeoutSeconds;
                m.AllowedStatusCodes = AllowedStatusCodesParser.Normalize(form.AllowedStatusCodes);
                m.ContentType = string.IsNullOrWhiteSpace(form.ContentType) ? null : form.ContentType.Trim();
                m.Body = form.Body;

                // Reset scheduling to "run soon" when config changes.
                m.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(m.IntervalSeconds, 5, 24 * 60 * 60));
            }

            var replaceChildren = !heartbeat;
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // Replace children with set-based deletes to avoid per-row optimistic concurrency exceptions.
                if (replaceChildren && replaceHeaders)
                {
                    await db.MonitorHeaders.Where(h => h.MonitorId == m.Id).ExecuteDeleteAsync(cancellationToken);
                    db.MonitorHeaders.AddRange(HeaderRows(form, m.Id));
                }

                if (replaceChildren && replaceMatchRules)
                {
                    await db.MonitorMatchRules.Where(r => r.MonitorId == m.Id).ExecuteDeleteAsync(cancellationToken);
                    db.MonitorMatchRules.AddRange(RuleRows(form, m.Id));
                }

                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return MonitorUpdateOutcome.Updated;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request updated or deleted this monitor since the caller loaded it.
                if (expectedStamp is not null || attempt >= MaxUnconditionalAttempts)
                    return MonitorUpdateOutcome.Conflict;
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Deletes the monitor and its dependent rows (headers, match rules, runs, alert state cascade in the database).
    /// Set-based; with <paramref name="expectedStamp"/> it only deletes when the stamp still matches.
    /// </summary>
    public async Task<MonitorDeleteOutcome> DeleteAsync(Guid id, Guid? expectedStamp, CancellationToken cancellationToken)
    {
        var query = db.Monitors.Where(x => x.Id == id);
        if (expectedStamp is { } stamp)
            query = query.Where(x => x.ConcurrencyStamp == stamp);

        if (await query.ExecuteDeleteAsync(cancellationToken) > 0)
            return MonitorDeleteOutcome.Deleted;

        return expectedStamp is not null && await db.Monitors.AnyAsync(x => x.Id == id, cancellationToken)
            ? MonitorDeleteOutcome.Conflict
            : MonitorDeleteOutcome.NotFound;
    }

    private static List<MonitorHeader> HeaderRows(MonitorForm form, Guid monitorId)
    {
        var rows = new List<MonitorHeader>();
        for (var i = 0; i < Math.Min(form.HeaderNames.Length, form.HeaderValues.Length); i++)
        {
            var name = (form.HeaderNames[i] ?? string.Empty).Trim();
            var value = (form.HeaderValues[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            rows.Add(new MonitorHeader { MonitorId = monitorId, Name = name, Value = value });
        }

        return rows;
    }

    private static List<MonitorMatchRule> RuleRows(MonitorForm form, Guid monitorId)
    {
        var rows = new List<MonitorMatchRule>();
        for (var i = 0; i < Math.Min(form.MatchModes.Length, form.MatchPatterns.Length); i++)
        {
            var pattern = (form.MatchPatterns[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(pattern) || form.MatchModes[i] == ContentMatchMode.None)
                continue;
            rows.Add(new MonitorMatchRule { MonitorId = monitorId, Mode = form.MatchModes[i], Pattern = pattern });
        }

        return rows;
    }
}
