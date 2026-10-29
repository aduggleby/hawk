// <file>
// <summary>
// "ApiKey" authentication scheme for /api: Authorization: Bearer hawk_... only. The scheme is never a default, so
// browser cookies cannot authorize /api requests and API keys cannot authorize pages. Principals carry no roles.
// </summary>
// </file>

using System.Security.Claims;
using System.Text.Encodings.Web;
using Hawk.Web.Data.ApiKeys;
using Hawk.Web.Services.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hawk.Web.Infrastructure;

public static class ApiKeyAuthentication
{
    public const string Scheme = "ApiKey";
    public const string ReadPolicy = "ApiRead";
    public const string WritePolicy = "ApiWrite";
    public const string KeyIdClaim = "hawk:key_id";
    public const string ScopeClaim = "hawk:scope";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Registers the scheme (without changing the default scheme) and the <c>ApiRead</c> / <c>ApiWrite</c> policies, both pinned to it.
    /// Kept out of <c>AddHawkIdentity</c>, which the design-time factories share.
    /// </summary>
    public static IServiceCollection AddHawkApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(Scheme, null);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(ScopeClaim, ScopeName(ApiKeyScope.Read), ScopeName(ApiKeyScope.Write)));
            options.AddPolicy(WritePolicy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(ScopeClaim, ScopeName(ApiKeyScope.Write)));
        });
        return services;
    }

    /// <summary>The key from an <c>Authorization: Bearer</c> header. The query string is never consulted.</summary>
    public static bool TryReadKey(HttpRequest request, out string key)
    {
        key = string.Empty;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var value = header[BearerPrefix.Length..].Trim();
        if (value.Length == 0) return false;
        key = value;
        return true;
    }

    internal static string ScopeName(ApiKeyScope scope) => scope == ApiKeyScope.Write ? "write" : "read";
}

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiKeyAuthentication.TryReadKey(Request, out var presented))
            return AuthenticateResult.NoResult();

        // A database failure here propagates to UseHawkApiErrors (500), not to a 401.
        var validated = await keys.ValidateAsync(presented, Context.RequestAborted);
        if (validated is null)
            return AuthenticateResult.Fail("Invalid API key.");

        await keys.TouchAsync(validated.Key.Id, Context.RequestAborted);
        Logger.LogDebug("API key {KeyId} authenticated.", validated.Key.Id);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, validated.User.Id),
            new Claim(ClaimTypes.Name, validated.User.Email ?? validated.User.UserName ?? validated.User.Id),
            new Claim(ClaimTypes.Email, validated.User.Email ?? string.Empty),
            new Claim(ApiKeyAuthentication.KeyIdClaim, validated.Key.Id.ToString()),
            new Claim(ApiKeyAuthentication.ScopeClaim, ApiKeyAuthentication.ScopeName(validated.Key.Scope)),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiErrors.WriteProblemAsync(Context, StatusCodes.Status401Unauthorized, "unauthorized",
            "Authentication required", "Send a valid API key as 'Authorization: Bearer hawk_...'.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteProblemAsync(Context, StatusCodes.Status403Forbidden, "insufficient_scope",
            "Insufficient scope", "This endpoint needs an API key with write scope.");
}
