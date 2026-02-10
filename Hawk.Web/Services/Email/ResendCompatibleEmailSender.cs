// <file>
// <summary>
// Resend-compatible email sender implementation.
// The user can override API base URL and API key via environment variables for Resend-like providers.
// </summary>
// </file>

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hawk.Web.Services.Email;

/// <summary>
/// Configuration for Resend-compatible email sending.
/// </summary>
public sealed class ResendCompatibleEmailOptions
{
    /// <summary>
    /// Base URL of the Resend-compatible service (e.g., https://api.resend.com).
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>
    /// API key used for Bearer authentication.
    /// </summary>
    public required string ApiKey { get; set; }

    /// <summary>
    /// Default "from" address.
    /// </summary>
    public required string From { get; set; }
}

/// <summary>
/// Sends emails using the Resend-compatible HTTP API.
/// </summary>
public sealed class ResendCompatibleEmailSender(HttpClient httpClient, IOptions<ResendCompatibleEmailOptions> options, ILogger<ResendCompatibleEmailSender> logger)
    : IEmailSender
{
    /// <inheritdoc />
    public async Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken)
    {
        if (to.Count == 0)
            return;

        var o = options.Value;
        var baseUri = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        var endpoint = new Uri(baseUri, "emails");

        logger.LogDebug("Sending email via Resend-compatible API (baseUrl={BaseUrl}, toCount={ToCount}, subject={Subject})", baseUri, to.Count, subject);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        // Resend API accepts "to" as string or array. We always send an array for consistency.
        var payload = new
        {
            from,
            to,
            subject,
            html
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await httpClient.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email send failed (transport error). baseUrl={BaseUrl}, toCount={ToCount}, subject={Subject}", baseUri, to.Count, subject);
            throw;
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                // Body is usually a short JSON error; useful for debugging misconfig/auth problems.
                logger.LogError("Email send failed: {Status} {Body}", (int)res.StatusCode, body);
                res.EnsureSuccessStatusCode();
            }

            logger.LogDebug("Email sent successfully: {Status} {Body}", (int)res.StatusCode, body);
        }
    }
}
