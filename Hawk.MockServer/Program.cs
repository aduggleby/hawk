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

var sentEmails = new ConcurrentQueue<object>();

app.MapGet("/", () => Results.Text("hawk-mockserver"));

app.MapGet("/ok", () => Results.Text("OK: Example Domain"));

app.MapGet("/nomatch", () => Results.Text("OK: but does not contain the expected phrase"));

app.MapGet("/error", () => Results.StatusCode(500));

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

app.MapPost("/emails", async (HttpContext ctx) =>
{
    // Resend-compatible-ish capture endpoint for E2E tests.
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync(ctx.RequestAborted);
    var item = new
    {
        at = DateTimeOffset.UtcNow,
        auth = ctx.Request.Headers.Authorization.ToString(),
        body
    };
    sentEmails.Enqueue(item);
    return Results.Json(new { id = Guid.NewGuid().ToString("n") });
});

app.MapGet("/emails", () => Results.Json(sentEmails.ToArray()));

app.Run();
