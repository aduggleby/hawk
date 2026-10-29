// <file>
// <summary>
// Bulk export and import of all monitors (admin migration/backup tool).
//
// Uses plain EF Core LINQ only and compares strings in C#, so it behaves the same on every database provider.
// Import is split into a side-effect free planning step (preview) and a commit step that re-plans from the raw
// JSON and writes everything in one transaction (all or nothing).
// </summary>
// </file>

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Hawk.Web.Data;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Monitoring;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Import;

/// <summary>
/// Outcome of planning a single monitor from an import file.
/// </summary>
public enum MonitorImportStatus
{
    /// <summary>The monitor passed validation and will be created.</summary>
    Valid,

    /// <summary>The monitor failed validation.</summary>
    Invalid,

    /// <summary>
    /// A monitor with the same name and URL already exists (or appeared earlier in the file). Heartbeats with a token
    /// are duplicates only when the token is already known.
    /// </summary>
    Duplicate,
}

/// <summary>
/// Planned handling of one monitor in an import file.
/// </summary>
/// <param name="Name">Monitor name as written in the file.</param>
/// <param name="Url">Monitor URL as written in the file.</param>
/// <param name="Status">Planned outcome.</param>
/// <param name="Error">Validation error when <paramref name="Status"/> is <see cref="MonitorImportStatus.Invalid"/>.</param>
/// <param name="OwnerEmail">Owner email from the file, if any.</param>
/// <param name="OwnerMatched">True when <paramref name="OwnerEmail"/> matched an existing user.</param>
/// <param name="PausedOrDisabledAtSource">True when the source monitor was paused or disabled.</param>
/// <param name="Monitor">Entity to insert (only for valid monitors).</param>
/// <param name="Warnings">Non-blocking problems (for example an unparsable regex).</param>
/// <param name="Group">Group the monitor will be in: canonicalized for valid monitors, normalized from the file otherwise.</param>
public sealed record PlannedMonitor(
    string Name,
    string Url,
    MonitorImportStatus Status,
    string? Error,
    string? OwnerEmail,
    bool OwnerMatched,
    bool PausedOrDisabledAtSource,
    MonitorEntity? Monitor,
    IReadOnlyList<string>? Warnings = null,
    string? Group = null);

/// <summary>
/// Result of planning an import file. Contains no database side effects.
/// </summary>
public sealed class MonitorImportPlan
{
    /// <summary>Parse error for the whole file, if it could not be read.</summary>
    public string? ParseError { get; init; }

    /// <summary>Planned monitors in file order.</summary>
    public IReadOnlyList<PlannedMonitor> Items { get; init; } = [];

    /// <summary>Total monitors in the file.</summary>
    public int Total => Items.Count;

    /// <summary>Monitors that will be created.</summary>
    public int ValidCount => Items.Count(i => i.Status == MonitorImportStatus.Valid);

    /// <summary>Monitors that failed validation.</summary>
    public int InvalidCount => Items.Count(i => i.Status == MonitorImportStatus.Invalid);

    /// <summary>Monitors that will be skipped as duplicates.</summary>
    public int DuplicateCount => Items.Count(i => i.Status == MonitorImportStatus.Duplicate);

    /// <summary>Valid monitors that carry at least one warning.</summary>
    public int WarningCount => Items.Count(i => i.Warnings is { Count: > 0 });

    /// <summary>Valid monitors whose owner email matched an existing user.</summary>
    public int OwnersMatched => Items.Count(i => i.Status == MonitorImportStatus.Valid && i.OwnerMatched);

    /// <summary>Valid monitors that will be assigned to the importing admin.</summary>
    public int OwnersAssignedToAdmin => ValidCount - OwnersMatched;

    /// <summary>True when the plan can be committed (parsed, and no invalid monitor).</summary>
    public bool CanImport => ParseError is null && InvalidCount == 0;
}

/// <summary>
/// Result of committing an import.
/// </summary>
/// <param name="Imported">Monitors created.</param>
/// <param name="SkippedDuplicates">Monitors skipped as duplicates.</param>
/// <param name="Plan">The plan that was executed.</param>
public sealed record MonitorImportResult(int Imported, int SkippedDuplicates, MonitorImportPlan Plan);

/// <summary>
/// Exports and imports all monitors.
/// </summary>
public sealed class MonitorTransferService(ApplicationDbContext db, IHostEnvironment env)
{
    /// <summary>Maximum accepted import size in bytes.</summary>
    public const int MaxImportBytes = 2 * 1024 * 1024;

    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    /// <summary>
    /// Serializes every monitor of every user (ordered by name) as a v3 export document.
    /// </summary>
    public async Task<string> ExportAllJsonAsync(CancellationToken cancellationToken)
    {
        var monitors = await db.Monitors
            .AsNoTracking()
            .Include(m => m.Headers)
            .Include(m => m.MatchRules)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Sort in memory (ordinal) so the file is identical regardless of database collation.
        monitors = monitors
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Url, StringComparer.Ordinal)
            .ThenBy(m => m.HeartbeatToken, StringComparer.Ordinal)
            .ToList();

        var ownerIds = monitors.Select(m => m.CreatedByUserId).OfType<string>().Distinct().ToList();
        var emails = await db.Users
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        var envelope = new MonitorExportEnvelope
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Monitors = monitors
                .Select(m => MonitorJsonPort.ToModel(
                    m,
                    m.CreatedByUserId is not null && emails.TryGetValue(m.CreatedByUserId, out var email) ? email : null))
                .ToList(),
        };

        return JsonSerializer.Serialize(envelope, MonitorJsonPort.JsonOptions);
    }

    /// <summary>
    /// Builds a dry-run plan for an import file without writing anything.
    /// </summary>
    public async Task<MonitorImportPlan> PlanAsync(
        string json,
        string? adminUserId,
        bool createPaused,
        CancellationToken cancellationToken)
    {
        // Browsers turn LF into CRLF in hidden fields; measure the normalized text so confirm matches the preview.
        if (Encoding.UTF8.GetByteCount(json.Replace("\r\n", "\n")) > MaxImportBytes)
            return new MonitorImportPlan { ParseError = "Import file is larger than 2 MB." };

        var existing = await db.Monitors
            .AsNoTracking()
            .Select(m => new { m.Name, m.Url, m.HeartbeatToken, m.GroupName })
            .ToListAsync(cancellationToken);
        var existingGroups = existing.Select(m => m.GroupName).OfType<string>().Distinct().ToList();
        var existingKeys = existing
            .Select(m => DuplicateKey(m.Name, m.Url))
            .Concat(existing.Where(m => m.HeartbeatToken is not null).Select(m => TokenKey(m.HeartbeatToken!)));

        var users = await db.Users
            .AsNoTracking()
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);
        var userIdsByEmail = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in users)
        {
            if (!string.IsNullOrWhiteSpace(u.Email))
                userIdsByEmail.TryAdd(u.Email.Trim(), u.Id);
        }

        return Plan(json, existingKeys, userIdsByEmail, adminUserId, createPaused, env, existingGroups);
    }

    /// <summary>
    /// Re-plans the given JSON and, if every non-duplicate monitor is valid, inserts all of them in one transaction.
    /// Returns the plan with <c>Imported = 0</c> when the plan cannot be imported.
    /// </summary>
    public async Task<MonitorImportResult> ImportAsync(
        string json,
        string? adminUserId,
        bool createPaused,
        CancellationToken cancellationToken)
    {
        // Serialize plan + save so a double submit or concurrent confirm cannot insert the same monitors twice
        // (there is no unique index on name + URL). Single-instance lock; the plan is re-computed inside it.
        await ImportLock.WaitAsync(cancellationToken);
        try
        {
            var plan = await PlanAsync(json, adminUserId, createPaused, cancellationToken);
            if (!plan.CanImport)
                return new MonitorImportResult(0, plan.DuplicateCount, plan);

            var toInsert = plan.Items
                .Where(i => i.Status == MonitorImportStatus.Valid && i.Monitor is not null)
                .Select(i => i.Monitor!)
                .ToList();

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Monitors.AddRange(toInsert);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new MonitorImportResult(toInsert.Count, plan.DuplicateCount, plan);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    /// <summary>
    /// Pure planning step: parses, de-duplicates (case-insensitively on name + URL), validates and maps owners.
    /// </summary>
    /// <param name="json">Raw import JSON.</param>
    /// <param name="existingKeys">Duplicate keys (see <see cref="DuplicateKey"/>, plus <see cref="TokenKey"/> for heartbeats) of monitors already present.</param>
    /// <param name="userIdsByEmail">Existing user ids keyed by email (case-insensitive comparer expected).</param>
    /// <param name="adminUserId">Fallback owner.</param>
    /// <param name="createPaused">Force imported monitors to be paused.</param>
    /// <param name="env">Host environment used by form validation.</param>
    /// <param name="existingGroups">Group names already used by existing monitors; imported groups take their spelling (ignoring case).</param>
    public static MonitorImportPlan Plan(
        string json,
        IEnumerable<string> existingKeys,
        IReadOnlyDictionary<string, string> userIdsByEmail,
        string? adminUserId,
        bool createPaused,
        IHostEnvironment env,
        IEnumerable<string>? existingGroups = null)
    {
        if (!MonitorJsonPort.TryParse(json, out var envelope, out var parseError) || envelope is null)
            return new MonitorImportPlan { ParseError = parseError ?? "Invalid monitor JSON." };

        var seen = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        var items = new List<PlannedMonitor>(envelope.Monitors.Count);

        // Group spellings by lower-cased name: existing groups first (ordinal-first spelling wins), then the first valid file entry.
        var groupSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spellings in (existingGroups ?? []).GroupBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            var display = MonitorGroups.DisplayName(spellings)!;
            groupSpellings[display] = display;
        }

        foreach (var model in envelope.Monitors)
        {
            var name = model.Name ?? string.Empty;
            var url = model.Url ?? string.Empty;
            var ownerEmail = string.IsNullOrWhiteSpace(model.OwnerEmail) ? null : model.OwnerEmail.Trim();
            var pausedOrDisabled = !model.Enabled || model.IsPaused;

            // Heartbeat identity is the 128-bit token: the same token is the same monitor (re-import skips it, a move
            // keeps the URL) and same-name heartbeats with different tokens are distinct. Without a valid token fall
            // back to the name (heartbeat URLs are empty) and issue a new token on import.
            var heartbeat = model.Type == MonitorType.Heartbeat;
            var hasToken = HeartbeatTokens.TryNormalize(model.HeartbeatToken, out var heartbeatToken);
            var nameKey = DuplicateKey(name, heartbeat ? string.Empty : url);
            var isNew = heartbeat && hasToken ? seen.Add(TokenKey(heartbeatToken)) : seen.Add(nameKey);
            if (heartbeat && hasToken)
                seen.Add(nameKey);

            if (!isNew)
            {
                items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Duplicate, null, ownerEmail, false, pausedOrDisabled, null, Group: MonitorGroups.Normalize(model.Group)));
                continue;
            }

            string? ownerId = null;
            if (ownerEmail is not null)
                userIdsByEmail.TryGetValue(ownerEmail, out ownerId);

            if (!MonitorJsonPort.TryCreateMonitor(model, ownerId ?? adminUserId, env, out var monitor, out var error, createPaused))
            {
                items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Invalid, error, ownerEmail, ownerId is not null, pausedOrDisabled, null, Group: MonitorGroups.Normalize(model.Group)));
                continue;
            }

            if (monitor.GroupName is { } fileGroup)
                monitor.GroupName = groupSpellings.TryAdd(fileGroup, fileGroup) ? fileGroup : groupSpellings[fileGroup];

            items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Valid, null, ownerEmail, ownerId is not null, pausedOrDisabled, monitor, MonitorJsonPort.GetWarnings(model), monitor.GroupName));
        }

        return new MonitorImportPlan { Items = items };
    }

    /// <summary>
    /// Duplicate key for a heartbeat token (tokens are normalized lowercase hex).
    /// </summary>
    public static string TokenKey(string token) => $"token:{token}";

    /// <summary>
    /// Duplicate key for a monitor: trimmed name and URL (compared case-insensitively by the caller's set).
    /// </summary>
    public static string DuplicateKey(string? name, string? url) =>
        $"{(name ?? string.Empty).Trim()}\n{(url ?? string.Empty).Trim()}";
}
