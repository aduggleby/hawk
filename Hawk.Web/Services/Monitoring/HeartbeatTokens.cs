// <file>
// <summary>
// Heartbeat helpers: secret URL tokens and the deadline math shared by create, import, resume and edit.
// </summary>
// </file>

using System.Security.Cryptography;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Heartbeat token and deadline helpers.
/// </summary>
public static class HeartbeatTokens
{
    /// <summary>Token length in characters (128 bits as lowercase hex).</summary>
    public const int Length = 32;

    /// <summary>
    /// Creates a new random token: 32 lowercase hex characters. One provider-neutral representation for
    /// lookup, the unique index, import and rate limiting.
    /// </summary>
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length / 2));

    /// <summary>
    /// Lowercases <paramref name="raw"/> and checks it is exactly 32 hex characters. Callers must do this
    /// before any database lookup.
    /// </summary>
    public static bool TryNormalize(string? raw, out string token)
    {
        token = string.Empty;
        if (raw is not { Length: Length })
            return false;

        var lower = raw.ToLowerInvariant();
        foreach (var c in lower)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        token = lower;
        return true;
    }

    /// <summary>
    /// The "since ..." part of a missed-ping reason. The board parses this text back
    /// (<see cref="BoardAttention.FindPingTime"/>) to show the time in the browser's zone, so keep the two in step.
    /// </summary>
    public static string MissSince(DateTimeOffset? lastPing) =>
        lastPing is { } t ? $"since {t.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" : "since monitoring started";

    /// <summary>
    /// The deadline for a new, imported, resumed or edited heartbeat: a full period plus grace from <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset InitialDeadline(DateTimeOffset now, int periodSeconds, int graceSeconds) =>
        now.AddSeconds(periodSeconds + graceSeconds);
}
