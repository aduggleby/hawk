// <file>
// <summary>
// UrlChecker redirect handling against a local HttpListener: follow (default) returns the final response, no-follow returns the 3xx itself.
// </summary>
// </file>

using System.Net;
using Hawk.Web.Services;
using Hawk.Web.Services.Monitoring;

namespace Hawk.Tests;

public sealed class UrlCheckerRedirectTests : IAsyncLifetime
{
    private readonly HttpListener _listener = new();
    private string _base = string.Empty;
    private Task _loop = Task.CompletedTask;

    public Task InitializeAsync()
    {
        // Pick a free port, then listen on it.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                if (ctx.Request.Url!.AbsolutePath == "/redirect")
                {
                    ctx.Response.StatusCode = 301;
                    ctx.Response.RedirectLocation = "/final";
                    await ctx.Response.OutputStream.WriteAsync("moved here"u8.ToArray());
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync("final page"u8.ToArray());
                }

                ctx.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Close();
        await _loop;
    }

    private UrlCheckRequest Request(bool follow, params ContentMatchRule[] rules) => new(
        new Uri(_base + "/redirect"), HttpMethod.Get, new Dictionary<string, string>(), null, null,
        TimeSpan.FromSeconds(10), rules, FollowRedirects: follow);

    private static UrlChecker Checker(out HttpClient a, out HttpClient b)
    {
        a = new HttpClient();
        b = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        return new UrlChecker(a, b);
    }

    [Fact]
    public async Task Following_returns_the_final_response()
    {
        var checker = Checker(out var a, out var b);
        using var _ = a; using var __ = b;

        var res = await checker.CheckAsync(Request(follow: true), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Success);
    }

    [Fact]
    public async Task Not_following_returns_the_3xx_with_its_location_header_and_body()
    {
        var checker = Checker(out var a, out var b);
        using var _ = a; using var __ = b;

        var res = await checker.CheckAsync(Request(follow: false, new ContentMatchRule(ContentMatchMode.Contains, "moved here")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.False(res.Success);
        Assert.Equal("/final", res.ResponseHeaders["Location"]);
        Assert.All(res.MatchResults, m => Assert.True(m.Matched));
    }

    [Fact]
    public void A_3xx_counts_as_success_only_when_listed_in_the_allowed_status_codes()
    {
        Assert.True(AllowedStatusCodesParser.IsSuccessStatusCode(HttpStatusCode.MovedPermanently, "301"));
        Assert.False(AllowedStatusCodesParser.IsSuccessStatusCode(HttpStatusCode.MovedPermanently, null));
        Assert.False(AllowedStatusCodesParser.IsSuccessStatusCode(HttpStatusCode.Found, "301"));
    }

    [Fact]
    public async Task Not_following_without_a_no_redirect_client_is_an_error_result_not_a_silent_follow()
    {
        using var only = new HttpClient();
        var res = await new UrlChecker(only).CheckAsync(Request(follow: false), CancellationToken.None);

        Assert.False(res.Success);
        Assert.Null(res.StatusCode);
    }
}
