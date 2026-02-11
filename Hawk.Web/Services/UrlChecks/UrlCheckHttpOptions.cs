namespace Hawk.Web.Services.UrlChecks;

public sealed class UrlCheckHttpOptions
{
    // Can be either a full UA string or a preset key like "firefox".
    public string? UserAgent { get; set; }

    // Optional: allow presets to be defined/overridden in appsettings.
    public Dictionary<string, string> UserAgentPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, string> BuiltInUserAgentPresets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // "Standard enough" desktop UAs; these don't need to be exact, just non-bot.
            ["firefox"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
            ["chrome"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            ["edge"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0",
            ["safari"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 13_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.3 Safari/605.1.15",
            ["curl"] = "curl/8.5.0",
        };
}

