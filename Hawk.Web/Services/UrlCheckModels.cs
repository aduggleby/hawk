using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hawk.Web.Services;

public enum ContentMatchMode
{
    None = 0,
    Contains = 1,
    Regex = 2,
}

public sealed record ContentMatchRule(ContentMatchMode Mode, string Pattern)
{
    public static ContentMatchRule None() => new(ContentMatchMode.None, string.Empty);
}

public sealed record UrlCheckRequest(
    Uri Url,
    HttpMethod Method,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    string? Body,
    TimeSpan Timeout,
    IReadOnlyList<ContentMatchRule> MatchRules
);

public sealed record UrlCheckMatchResult(ContentMatchRule Rule, bool Matched, string? Details);

public sealed record UrlCheckResult(
    bool Success,
    HttpStatusCode? StatusCode,
    TimeSpan Duration,
    string? ErrorMessage,
    IReadOnlyList<UrlCheckMatchResult> MatchResults,
    string? ResponseBodySnippet
);

public interface IUrlChecker
{
    Task<UrlCheckResult> CheckAsync(UrlCheckRequest request, CancellationToken cancellationToken);
}

public sealed class UrlChecker(HttpClient httpClient) : IUrlChecker
{
    // Limit how much we buffer for matching/snippets. Keep it small to avoid storing huge bodies.
    private const int MaxBodyBytes = 256 * 1024;

    public async Task<UrlCheckResult> CheckAsync(UrlCheckRequest request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var httpRequest = new HttpRequestMessage(request.Method, request.Url);
            foreach (var (k, v) in request.Headers)
            {
                // Try "regular" headers first; fall back to content headers when we have content.
                if (!httpRequest.Headers.TryAddWithoutValidation(k, v))
                {
                    httpRequest.Content ??= new ByteArrayContent([]);
                    httpRequest.Content.Headers.TryAddWithoutValidation(k, v);
                }
            }

            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put || request.Method == HttpMethod.Patch)
            {
                var body = request.Body ?? string.Empty;
                var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType!;
                httpRequest.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var bodyText = await ReadBodyAsStringAsync(response, timeoutCts.Token);

            var matchResults = EvaluateMatches(request.MatchRules, bodyText);
            var ok = response.IsSuccessStatusCode && matchResults.All(m => m.Matched);

            sw.Stop();
            return new UrlCheckResult(
                Success: ok,
                StatusCode: response.StatusCode,
                Duration: sw.Elapsed,
                ErrorMessage: ok ? null : BuildFailureMessage(response.StatusCode, matchResults),
                MatchResults: matchResults,
                ResponseBodySnippet: MakeSnippet(bodyText)
            );
        }
        catch (OperationCanceledException oce) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new UrlCheckResult(
                Success: false,
                StatusCode: null,
                Duration: sw.Elapsed,
                ErrorMessage: $"Timeout after {request.Timeout.TotalSeconds:0.##}s: {oce.Message}",
                MatchResults: request.MatchRules.Select(r => new UrlCheckMatchResult(r, false, "Not evaluated (timeout)")).ToArray(),
                ResponseBodySnippet: null
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new UrlCheckResult(
                Success: false,
                StatusCode: null,
                Duration: sw.Elapsed,
                ErrorMessage: ex.Message,
                MatchResults: request.MatchRules.Select(r => new UrlCheckMatchResult(r, false, "Not evaluated (request error)")).ToArray(),
                ResponseBodySnippet: null
            );
        }
    }

    private static IReadOnlyList<UrlCheckMatchResult> EvaluateMatches(IReadOnlyList<ContentMatchRule> rules, string bodyText)
    {
        if (rules.Count == 0)
            return [];

        var results = new List<UrlCheckMatchResult>(rules.Count);
        foreach (var rule in rules)
        {
            if (rule.Mode == ContentMatchMode.None || string.IsNullOrEmpty(rule.Pattern))
            {
                results.Add(new UrlCheckMatchResult(rule, true, null));
                continue;
            }

            try
            {
                bool matched = rule.Mode switch
                {
                    ContentMatchMode.Contains => bodyText.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
                    ContentMatchMode.Regex => Regex.IsMatch(
                        bodyText,
                        rule.Pattern,
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
                        matchTimeout: TimeSpan.FromSeconds(2)),
                    _ => true
                };
                results.Add(new UrlCheckMatchResult(rule, matched, matched ? null : "Not found"));
            }
            catch (Exception ex)
            {
                results.Add(new UrlCheckMatchResult(rule, false, $"Match error: {ex.Message}"));
            }
        }
        return results;
    }

    private static string BuildFailureMessage(HttpStatusCode statusCode, IReadOnlyList<UrlCheckMatchResult> matchResults)
    {
        var parts = new List<string>();
        if ((int)statusCode < 200 || (int)statusCode >= 300)
            parts.Add($"HTTP {(int)statusCode} ({statusCode})");

        var failed = matchResults.Where(m => !m.Matched).ToArray();
        if (failed.Length != 0)
            parts.Add($"{failed.Length} match rule(s) failed");

        return string.Join("; ", parts);
    }

    private static string? MakeSnippet(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return null;
        var trimmed = bodyText.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static async Task<string> ReadBodyAsStringAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            var take = Math.Min(read, MaxBodyBytes - total);
            if (take > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, take), cancellationToken);
                total += take;
            }
            if (total >= MaxBodyBytes) break;
        }

        // Most pages we're checking are UTF-8; good enough for v1.
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

