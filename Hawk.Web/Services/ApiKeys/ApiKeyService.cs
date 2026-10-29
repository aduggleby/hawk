// <file>
// <summary>
// Issues, validates, lists and revokes per-user API keys. Keys are stored only as SHA-256 hashes.
// </summary>
// </file>

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hawk.Web.Data;
using Hawk.Web.Data.ApiKeys;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Services.ApiKeys;

/// <summary>A newly issued key. <see cref="Plaintext"/> is shown once and never stored.</summary>
public sealed record CreatedApiKey(ApiKey Key, string Plaintext);

/// <summary>A key that passed validation, with its user.</summary>
public sealed record ValidatedApiKey(ApiKey Key, IdentityUser User);

/// <summary>
/// API key service.
/// </summary>
public sealed partial class ApiKeyService(ApplicationDbContext db)
{
    private const string Prefix = "hawk_";
    private const int DisplayPrefixLength = 12;

    /// <summary>Minimum time between <c>LastUsedAt</c> writes for one key.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    /// <summary>True when <paramref name="key"/> has the shape of an issued key (no database access).</summary>
    public static bool IsWellFormed(string key) => KeyFormat().IsMatch(key);

    [GeneratedRegex("^hawk_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyFormat();

    /// <summary>Creates a key and returns its plaintext once.</summary>
    public async Task<CreatedApiKey> CreateAsync(string userId, string name, ApiKeyScope scope, CancellationToken cancellationToken = default)
    {
        var plaintext = Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            UserId = userId,
            Name = name,
            Scope = scope,
            DisplayPrefix = plaintext[..DisplayPrefixLength],
            SecretHash = Hash(plaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiKey(key, plaintext);
    }

    /// <summary>The user's active (not revoked) keys, newest first.</summary>
    public async Task<List<ApiKey>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
        (await db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId && k.RevokedAt == null).ToListAsync(cancellationToken))
            .OrderByDescending(k => k.CreatedAt).ToList();

    /// <summary>Revokes one of the user's own keys. False when there is no such active key.</summary>
    public async Task<bool> RevokeAsync(string userId, Guid keyId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            return await db.ApiKeys.Where(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), cancellationToken) > 0;
        }

        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null, cancellationToken);
        if (key is null)
            return false;
        key.RevokedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Revokes all of the user's active keys.</summary>
    public async Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), cancellationToken);
            return;
        }

        // Branch: EF InMemory (unit tests) does not support ExecuteUpdate.
        foreach (var key in await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null).ToListAsync(cancellationToken))
            key.RevokedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resets the password with Identity and revokes all of the user's keys atomically: on relational providers both run in one
    /// transaction on the shared scoped context (UserManager's store uses the same <see cref="ApplicationDbContext"/>), so a failing
    /// revoke rolls the reset back. Keys are revoked only after a successful reset. Once the reset succeeded the revoke and commit
    /// ignore request cancellation. EF InMemory (unit tests) has no transactions and runs the plain sequence.
    /// </summary>
    public async Task<IdentityResult> ResetPasswordAndRevokeAsync(
        UserManager<IdentityUser> users, IdentityUser user, string token, string newPassword)
    {
        if (!db.Database.IsRelational())
        {
            var plain = await users.ResetPasswordAsync(user, token, newPassword);
            if (plain.Succeeded)
                await RevokeAllAsync(user.Id, CancellationToken.None);
            return plain;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
            return result; // disposing the transaction rolls back anything Identity wrote

        await RevokeAllAsync(user.Id, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return result;
    }

    /// <summary>
    /// Returns the key and its user, or null for a malformed, unknown or revoked key or a missing user.
    /// Lockout is deliberately ignored.
    /// </summary>
    public async Task<ValidatedApiKey?> ValidateAsync(string? presented, CancellationToken cancellationToken = default)
    {
        if (presented is null || !KeyFormat().IsMatch(presented))
            return null;

        var hash = Hash(presented);
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.SecretHash == hash && k.RevokedAt == null, cancellationToken);
        if (key is null)
            return null;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == key.UserId, cancellationToken);
        return user is null ? null : new ValidatedApiKey(key, user);
    }

    /// <summary>Sets <c>LastUsedAt</c> to now, at most once per <see cref="TouchInterval"/>.</summary>
    public async Task TouchAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TouchInterval;
        if (db.Database.IsRelational())
        {
            await db.ApiKeys.Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt < cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
            return;
        }

        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is not null && (key.LastUsedAt is null || key.LastUsedAt < cutoff))
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
