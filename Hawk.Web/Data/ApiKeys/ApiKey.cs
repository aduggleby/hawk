using System.ComponentModel.DataAnnotations;

namespace Hawk.Web.Data.ApiKeys;

/// <summary>
/// What an API key may do.
/// </summary>
public enum ApiKeyScope
{
    Read,
    Write,
}

/// <summary>
/// A per-user API key. Only the SHA-256 hash of the secret is stored.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(450)]
    public required string UserId { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    /// <summary>Stored as a string (max 16).</summary>
    public ApiKeyScope Scope { get; set; }

    /// <summary>Non-secret first 12 characters of the key, for display.</summary>
    [MaxLength(16)]
    public required string DisplayPrefix { get; set; }

    /// <summary>Lowercase hex SHA-256 of the full key (unique).</summary>
    [MaxLength(64)]
    public required string SecretHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
