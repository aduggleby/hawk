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
using Hawk.Web.Pages.Monitors;
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

    /// <summary>A monitor with the same name and URL already exists (or appeared earlier in the file).</summary>
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
public sealed record PlannedMonitor(
    string Name,
    string Url,
    MonitorImportStatus Status,
    string? Error,
    string? OwnerEmail,
    bool OwnerMatched,
    bool PausedOrDisabledAtSource,
    MonitorEntity? Monitor);

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
    /// Serializes every monitor of every user (ordered by name) as a v2 export document.
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
            .Select(m => new { m.Name, m.Url })
            .ToListAsync(cancellationToken);
        var existingKeys = existing.Select(m => DuplicateKey(m.Name, m.Url));

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

        return Plan(json, existingKeys, userIdsByEmail, adminUserId, createPaused, env);
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
        }    }

    /// <summary>
    /// Pure planning step: parses, de-duplicates (case-insensitively on name + URL), validates and maps owners.
    /// </summary>
    /// <param name="json">Raw import JSON.</param>
    /// <param name="existingKeys">Duplicate keys (see <see cref="DuplicateKey"/>) of monitors already present.</param>
    /// <param name="userIdsByEmail">Existing user ids keyed by email (case-insensitive comparer expected).</param>
    /// <param name="adminUserId">Fallback owner.</param>
    /// <param name="createPaused">Force imported monitors to be paused.</param>
    /// <param name="env">Host environment used by form validation.</param>
    public static MonitorImportPlan Plan(
        string json,
        IEnumerable<string> existingKeys,
        IReadOnlyDictionary<string, string> userIdsByEmail,
        string? adminUserId,
        bool createPaused,
        IHostEnvironment env)
    {
        if (!MonitorJsonPort.TryParse(json, out var envelope, out var parseError) || envelope is null)
            return new MonitorImportPlan { ParseError = parseError ?? "Invalid monitor JSON." };

        var seen = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        var items = new List<PlannedMonitor>(envelope.Monitors.Count);

        foreach (var model in envelope.Monitors)
        {
            var name = model.Name ?? string.Empty;
            var url = model.Url ?? string.Empty;
            var ownerEmail = string.IsNullOrWhiteSpace(model.OwnerEmail) ? null : model.OwnerEmail.Trim();
            var pausedOrDisabled = !model.Enabled || model.IsPaused;

            if (!seen.Add(DuplicateKey(name, url)))
            {
                items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Duplicate, null, ownerEmail, false, pausedOrDisabled, null));
                continue;
            }

            string? ownerId = null;
            if (ownerEmail is not null)
                userIdsByEmail.TryGetValue(ownerEmail, out ownerId);

            if (!MonitorJsonPort.TryCreateMonitor(model, ownerId ?? adminUserId, env, out var monitor, out var error, createPaused))
            {
                items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Invalid, error, ownerEmail, ownerId is not null, pausedOrDisabled, null));
                continue;
            }

            items.Add(new PlannedMonitor(name, url, MonitorImportStatus.Valid, null, ownerEmail, ownerId is not null, pausedOrDisabled, monitor));
        }

        return new MonitorImportPlan { Items = items };
    }

    /// <summary>
    /// Duplicate key for a monitor: trimmed name and URL (compared case-insensitively by the caller's set).
    /// </summary>
    public static string DuplicateKey(string? name, string? url) =>
        $"{(name ?? string.Empty).Trim()}\n{(url ?? string.Empty).Trim()}";
}
