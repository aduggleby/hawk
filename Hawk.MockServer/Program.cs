// <file>
// <summary>
// Deterministic mock HTTP server used by E2E tests and local development.
// Provides endpoints for simulating success, content mismatches, server errors, and timeouts.
// Also provides a Resend-compatible /emails endpoint for capturing alert payloads.
// </summary>
// </file>

using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var flakyCounter = 0;

app.MapGet("/", () => Results.Text("hawk-mockserver"));

app.MapGet("/ok", () => Results.Text("OK: Example Domain"));

app.MapGet("/nomatch", () => Results.Text("OK: but does not contain the expected phrase"));

app.MapGet("/error", () => Results.StatusCode(500));

app.MapGet("/flaky", () =>
{
    // Intermittent endpoint: alternates between success and failure.
    var n = Interlocked.Increment(ref flakyCounter);
    return (n % 2) == 0
        ? Results.Text($"FLAKY_OK #{n}")
        : Results.StatusCode(503);
});

app.MapGet("/slow", async (HttpContext ctx) =>
{
    // Branch: simulate a slow upstream that exceeds typical timeouts.
    await Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted);
    return Results.Text("slow");
});

app.MapPost("/echo", async (HttpContext ctx) =>
{
    // Echo endpoint for validating POST body/header behavior.
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync(ctx.RequestAborted);
    var hdr = ctx.Request.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
    return Results.Json(new { body, headers = hdr, contentType = ctx.Request.ContentType });
});

if ((app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) &&
    app.Configuration.GetValue<bool>("Hawk:MockServer:EmailCaptureEnabled"))
{
    var sentEmails = new ConcurrentQueue<object>();

    app.MapPost("/emails", async (HttpContext ctx) =>
    {
        // Resend-compatible capture endpoint for local development and E2E tests.
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync(ctx.RequestAborted);
        sentEmails.Enqueue(new { at = DateTimeOffset.UtcNow, body });
        return Results.Json(new { id = Guid.NewGuid().ToString("n") });
    });

    app.MapGet("/emails", () => Results.Json(sentEmails.ToArray()));

    app.MapPost("/emails/reset", () =>
    {
        // Clear captured emails between E2E tests.
        while (sentEmails.TryDequeue(out _)) { }
        return Results.Ok();
    });
}

app.Run();

// Lets endpoint tests select this assembly without colliding with Hawk.Web's Program.
namespace Hawk.MockServer
{
    public sealed class MockServerEntryPoint { }
}
