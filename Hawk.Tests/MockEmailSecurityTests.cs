using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hawk.MockServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Hawk.Tests;

public sealed class MockEmailSecurityTests
{
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    [InlineData("Testing", false)]
    public async Task Email_routes_are_absent_without_an_allowed_environment_and_opt_in(
        string environment, bool captureEnabled)
    {
        using var factory = CreateFactory(environment, captureEnabled);
        using var client = factory.CreateClient();

        using var capture = await client.PostAsync("/emails", JsonBody("{\"subject\":\"test\"}"));
        using var list = await client.GetAsync("/emails");
        using var reset = await client.PostAsync("/emails/reset", null);

        Assert.Equal(HttpStatusCode.NotFound, capture.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);

        using var ok = await client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("OK: Example Domain", await ok.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Enabled_capture_preserves_email_body_but_never_stores_bearer_token(string environment)
    {
        using var factory = CreateFactory(environment, captureEnabled: true);
        using var client = factory.CreateClient();
        const string body = "{\"to\":\"operator@example.com\",\"html\":\"<a href='https://example.com/confirm'>Confirm</a>\"}";
        const string bearerToken = "test-bearer-secret";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/emails")
        {
            Content = JsonBody(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var capture = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);

        using var list = await client.GetAsync("/emails");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var responseJson = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(bearerToken, responseJson, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(responseJson);
        var email = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(body, email.GetProperty("body").GetString());
        Assert.True(email.TryGetProperty("at", out _));
        Assert.False(email.TryGetProperty("auth", out _));

        using var reset = await client.PostAsync("/emails/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var afterReset = await client.GetAsync("/emails");
        using var emptyDocument = JsonDocument.Parse(await afterReset.Content.ReadAsStringAsync());
        Assert.Empty(emptyDocument.RootElement.EnumerateArray());

        using var echo = await client.PostAsync("/echo", JsonBody("still available"));
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
    }

    private static WebApplicationFactory<MockServerEntryPoint> CreateFactory(string environment, bool captureEnabled) =>
        new WebApplicationFactory<MockServerEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Hawk:MockServer:EmailCaptureEnabled"] = captureEnabled.ToString()
                }));
        });

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");
}
