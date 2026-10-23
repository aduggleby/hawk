using Hawk.Web.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawk.Tests;

public class EmailSetupTests
{
    [Fact]
    public async Task Resolved_email_sender_uses_the_configured_typed_client_timeout()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hawk:Resend:BaseUrl"] = "http://email.invalid",
            ["Hawk:Resend:ApiKey"] = "k",
            ["Hawk:Resend:From"] = "Hawk <hawk@localhost>",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHawkEmail(config);
        // A transport that never answers: only the HttpClient timeout can end the request.
        services.AddHttpClient(nameof(IEmailSender)).ConfigurePrimaryHttpMessageHandler(() => new HangingHandler());
        await using var sp = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IEmailSender));
        Assert.Equal(TimeSpan.FromSeconds(5), sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEmailSender)).Timeout);

        await using var scope = sp.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync("a@b.c", ["d@e.f"], "s", "h", CancellationToken.None));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
