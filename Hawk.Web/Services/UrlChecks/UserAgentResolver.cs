namespace Hawk.Web.Services.UrlChecks;

internal static class UserAgentResolver
{
    public static string Resolve(IConfiguration config, string? raw)
    {
        var uaRaw = raw?.Trim();
        if (string.IsNullOrWhiteSpace(uaRaw))
            uaRaw = "firefox";

        // Allow overriding presets from config.
        var fromConfigPreset = config[$"Hawk:UrlChecks:UserAgentPresets:{uaRaw}"]?.Trim();
        if (!string.IsNullOrWhiteSpace(fromConfigPreset))
            return fromConfigPreset;

        // Built-in presets.
        if (UrlCheckHttpOptions.BuiltInUserAgentPresets.TryGetValue(uaRaw, out var preset))
            return preset;

        // Otherwise treat as a full UA string.
        return uaRaw;
    }
}

