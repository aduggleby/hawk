// <file>
// <summary>
// Unit tests (EF InMemory) for API key issuing, validation, listing and revocation (R1, R2, R5, R6).
// </summary>
// </file>

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hawk.Web.Data;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Services.ApiKeys;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="ApiKeyService"/>.
/// </summary>
public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task Created_key_has_expected_format_and_only_its_hash_is_stored()
    {
        var (db, service) = Create();
        var created = await service.CreateAsync("u1", "laptop", ApiKeyScope.Write);

        Assert.Matches(new Regex("^hawk_[A-Za-z0-9_-]{43}$"), created.Plaintext);
        var stored = await db.ApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(created.Plaintext))), stored.SecretHash);
        Assert.Equal(created.Plaintext[..12], stored.DisplayPrefix);
        Assert.Equal("laptop", stored.Name);
        Assert.Equal(ApiKeyScope.Write, stored.Scope);
        Assert.Equal("u1", stored.UserId);
        Assert.DoesNotContain(created.Plaintext, stored.SecretHash + stored.DisplayPrefix + stored.Name);
    }

    [Fact]
    public async Task Two_keys_are_distinct()
    {
        var (_, service) = Create();
        var a = await service.CreateAsync("u1", "a", ApiKeyScope.Read);
        var b = await service.CreateAsync("u1", "b", ApiKeyScope.Read);
        Assert.NotEqual(a.Plaintext, b.Plaintext);
    }

    [Fact]
    public async Task Validate_accepts_issued_key_and_returns_key_and_user()
    {
        var (db, service) = Create();
        await AddUserAsync(db, "u1");
        var created = await service.CreateAsync("u1", "k", ApiKeyScope.Read);

        var result = await service.ValidateAsync(created.Plaintext);

        Assert.NotNull(result);
        Assert.Equal(created.Key.Id, result.Key.Id);
        Assert.Equal("u1", result.User.Id);
    }

    [Fact]
    public async Task Validate_rejects_revoked_unknown_altered_unprefixed_and_malformed_keys()
    {
        var (db, service) = Create();
        await AddUserAsync(db, "u1");
        var revoked = await service.CreateAsync("u1", "revoked", ApiKeyScope.Read);
        var live = await service.CreateAsync("u1", "live", ApiKeyScope.Read);
        Assert.True(await service.RevokeAsync("u1", revoked.Key.Id));

        Assert.Null(await service.ValidateAsync(revoked.Plaintext));
        Assert.Null(await service.ValidateAsync("hawk_" + new string('A', 43)));

        var last = live.Plaintext[^1];
        var altered = live.Plaintext[..^1] + (last == 'A' ? 'B' : 'A');
        Assert.Null(await service.ValidateAsync(altered));
        Assert.Null(await service.ValidateAsync(live.Plaintext["hawk_".Length..]));
        Assert.Null(await service.ValidateAsync(live.Plaintext + "x"));
        Assert.Null(await service.ValidateAsync(""));
        Assert.Null(await service.ValidateAsync(null));
        Assert.NotNull(await service.ValidateAsync(live.Plaintext));
    }

    [Fact]
    public async Task Validate_rejects_key_whose_user_is_missing()
    {
        var (_, service) = Create();
        var created = await service.CreateAsync("ghost", "k", ApiKeyScope.Read);
        Assert.Null(await service.ValidateAsync(created.Plaintext));
    }

    [Fact]
    public async Task Validate_ignores_lockout()
    {
        var (db, service) = Create();
        var user = await AddUserAsync(db, "u1");
        user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
        await db.SaveChangesAsync();
        var created = await service.CreateAsync("u1", "k", ApiKeyScope.Read);

        Assert.NotNull(await service.ValidateAsync(created.Plaintext));
    }

    [Fact]
    public async Task List_returns_only_own_active_keys()
    {
        var (_, service) = Create();
        var a = await service.CreateAsync("u1", "a", ApiKeyScope.Read);
        var b = await service.CreateAsync("u1", "b", ApiKeyScope.Write);
        await service.CreateAsync("u2", "other", ApiKeyScope.Read);
        await service.RevokeAsync("u1", a.Key.Id);

        var list = await service.ListAsync("u1");

        Assert.Equal([b.Key.Id], list.Select(k => k.Id));
    }

    [Fact]
    public async Task Revoke_only_affects_own_keys()
    {
        var (db, service) = Create();
        var mine = await service.CreateAsync("u1", "mine", ApiKeyScope.Read);

        Assert.False(await service.RevokeAsync("u2", mine.Key.Id));
        Assert.Null((await db.ApiKeys.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.True(await service.RevokeAsync("u1", mine.Key.Id));
        Assert.NotNull((await db.ApiKeys.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.False(await service.RevokeAsync("u1", Guid.NewGuid()));
    }

    [Fact]
    public async Task Revoke_all_marks_every_active_key_of_that_user_and_none_of_another()
    {
        var (db, service) = Create();
        await service.CreateAsync("u1", "a", ApiKeyScope.Read);
        await service.CreateAsync("u1", "b", ApiKeyScope.Write);
        await service.CreateAsync("u2", "c", ApiKeyScope.Read);

        await service.RevokeAllAsync("u1");

        var keys = await db.ApiKeys.AsNoTracking().ToListAsync();
        Assert.All(keys.Where(k => k.UserId == "u1"), k => Assert.NotNull(k.RevokedAt));
        Assert.All(keys.Where(k => k.UserId == "u2"), k => Assert.Null(k.RevokedAt));
    }

    [Fact]
    public async Task Touch_writes_last_used_once_within_five_minutes()
    {
        var (db, service) = Create();
        var created = await service.CreateAsync("u1", "k", ApiKeyScope.Read);

        await service.TouchAsync(created.Key.Id);
        var first = (await db.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt;
        Assert.NotNull(first);

        await service.TouchAsync(created.Key.Id);
        Assert.Equal(first, (await db.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt);

        var old = DateTimeOffset.UtcNow.AddMinutes(-6);
        var tracked = await db.ApiKeys.SingleAsync();
        tracked.LastUsedAt = old;
        await db.SaveChangesAsync();
        await service.TouchAsync(created.Key.Id);
        Assert.True((await db.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAt > old.AddMinutes(5));
    }

    private static (ApplicationDbContext Db, ApiKeyService Service) Create()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        var db = new ApplicationDbContext(opts);
        return (db, new ApiKeyService(db));
    }

    private static async Task<IdentityUser> AddUserAsync(ApplicationDbContext db, string id)
    {
        var user = new IdentityUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
