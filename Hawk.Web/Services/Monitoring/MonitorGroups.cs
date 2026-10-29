// <file>
// <summary>
// Monitor group names: normalization, validation, case-insensitive canonicalization against existing groups, and the shared
// group filter. A group exists while at least one monitor uses it; there is no group entity.
// </summary>
// </file>

using Hawk.Web.Data;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Helpers for <see cref="MonitorEntity.GroupName"/>, used by pages, the API, imports and rename.
/// </summary>
public static class MonitorGroups
{
    /// <summary>Maximum group name length (matches the column).</summary>
    public const int MaxLength = 100;

    /// <summary>Trims the name; a blank name means no group (null).</summary>
    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Validates a group name after normalizing it: at most 100 characters, no NUL (PostgreSQL text rejects it).
    /// Blank is valid (no group). Returns an error message or null.
    /// </summary>
    public static string? Validate(string? name)
    {
        var normalized = Normalize(name);
        if (normalized is null)
            return null;
        if (normalized.Length > MaxLength)
            return $"Group must be at most {MaxLength} characters.";
        if (normalized.Contains('\0'))
            return "Group must not contain NUL characters.";
        return null;
    }

    /// <summary>
    /// Returns the spelling of an existing group that equals <paramref name="name"/> ignoring case, else <paramref name="name"/>.
    /// Compares in memory with OrdinalIgnoreCase (database collations differ). Monitor <paramref name="excludeMonitorId"/> and the group
    /// <paramref name="excludeGroup"/> (ignoring case) do not count as existing, so the only member of a group can change its casing.
    /// </summary>
    public static async Task<string?> CanonicalizeAsync(
        ApplicationDbContext db, string? name, Guid? excludeMonitorId = null, string? excludeGroup = null,
        CancellationToken cancellationToken = default)
    {
        if (name is null)
            return null;

        var query = db.Monitors.Where(m => m.GroupName != null);
        if (excludeMonitorId is { } id)
            query = query.Where(m => m.Id != id);
        var existing = await query.Select(m => m.GroupName!).Distinct().ToListAsync(cancellationToken);

        return existing
            .Where(g => excludeGroup is null || !string.Equals(g, excludeGroup, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g, StringComparer.Ordinal)
            .FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>Monitors in <paramref name="group"/>, compared case-insensitively (translates on SQL Server and PostgreSQL).</summary>
    public static IQueryable<MonitorEntity> InGroup(IQueryable<MonitorEntity> monitors, string group)
    {
        var lower = group.ToLower();
        return monitors.Where(m => m.GroupName != null && m.GroupName.ToLower() == lower);
    }

    /// <summary>
    /// Page anchor for a group section: <c>group-</c> + slug of the lowercased name + "-" + the first 6 hex characters of its
    /// SHA-256, so distinct names never share an anchor. Ungrouped (blank) is <c>group-none</c>.
    /// </summary>
    public static string Anchor(string? name)
    {
        var normalized = Normalize(name);
        if (normalized is null)
            return "group-none";
        var lower = normalized.ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lower)))[..6].ToLowerInvariant();
        return slug.Length == 0 ? $"group-{hash}" : $"group-{slug}-{hash}";
    }

    /// <summary>Distinct existing group names (ignoring case), sorted with OrdinalIgnoreCase; the ordinal-first spelling wins.</summary>
    public static async Task<List<string>> ListAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var names = await db.Monitors.Where(m => m.GroupName != null).Select(m => m.GroupName!).Distinct().ToListAsync(cancellationToken);
        return names
            .OrderBy(n => n, StringComparer.Ordinal)
            .DistinctBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
