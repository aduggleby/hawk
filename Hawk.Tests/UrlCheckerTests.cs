using Hawk.Web.Services;

namespace Hawk.Tests;

public class UrlCheckerTests
{
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

