// <file>
// <summary>
// Unit tests for the URL checker implementation.
// Uses https://example.com for stable, low-risk integration-style assertions.
// </summary>
// </file>

using Hawk.Web.Services;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="UrlChecker"/>.
/// </summary>
public class UrlCheckerTests
{
    /// <summary>
    /// Verifies a simple GET succeeds and content contains a known string.
    /// </summary>
    [Fact]
    public async Task Get_ExampleDotCom_ContainsMatch_Succeeds()
    {
        using var http = new HttpClient();
        var checker = new UrlChecker(http);

        var req = new UrlCheckRequest(
            Url: new Uri("https://example.com"),
            Method: HttpMethod.Get,
            Headers: new Dictionary<string, string>(),
            ContentType: null,
            Body: null,
            Timeout: TimeSpan.FromSeconds(15),
            MatchRules: [new ContentMatchRule(ContentMatchMode.Contains, "Example Domain")]
        );

        var res = await checker.CheckAsync(req, CancellationToken.None);

        Assert.True(res.StatusCode is not null);
        Assert.True((int)res.StatusCode! >= 200 && (int)res.StatusCode! < 300);
        Assert.True(res.Success);
        Assert.Contains(res.MatchResults, m => m.Rule.Mode == ContentMatchMode.Contains && m.Matched);
    }

    /// <summary>
    /// Verifies regex matching succeeds on a stable page.
    /// </summary>
    [Fact]
    public async Task Get_ExampleDotCom_RegexMatch_Succeeds()
    {
        using var http = new HttpClient();
        var checker = new UrlChecker(http);

        var req = new UrlCheckRequest(
            Url: new Uri("https://example.com"),
            Method: HttpMethod.Get,
            Headers: new Dictionary<string, string>(),
            ContentType: null,
            Body: null,
            Timeout: TimeSpan.FromSeconds(15),
            MatchRules: [new ContentMatchRule(ContentMatchMode.Regex, "Example\\s+Domain")]
        );

        var res = await checker.CheckAsync(req, CancellationToken.None);

        Assert.True(res.Success);
        Assert.Contains(res.MatchResults, m => m.Rule.Mode == ContentMatchMode.Regex && m.Matched);
    }

    /// <summary>
    /// Verifies non-GET requests return a deterministic failure result (but do not throw).
    /// </summary>
    [Fact]
    public async Task Post_ExampleDotCom_IsNotSuccess()
    {
        using var http = new HttpClient();
        var checker = new UrlChecker(http);

        var req = new UrlCheckRequest(
            Url: new Uri("https://example.com"),
            Method: HttpMethod.Post,
            Headers: new Dictionary<string, string> { ["X-Test"] = "hawk" },
            ContentType: "application/json",
            Body: "{\"ping\":true}",
            Timeout: TimeSpan.FromSeconds(15),
            MatchRules: [ContentMatchRule.None()]
        );

        var res = await checker.CheckAsync(req, CancellationToken.None);

        // example.com typically doesn't accept POST; we mainly assert the checker doesn't throw and returns a result.
        Assert.False(res.Success);
    }
}
