using Hawk.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawk.Tests;

public class RequestCorrelationTests
{
    [Theory]
    [InlineData("4f1c2a0e-7b3d-4e59-9c3a-1d2e3f405162", true)]
    [InlineData("abc.DEF_1:2-3", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789", false)]
    public void Validates_request_ids(string value, bool expected) =>
        Assert.Equal(expected, RequestCorrelation.IsValid(value));

    [Fact]
    public async Task Valid_header_becomes_the_trace_identifier_and_invalid_is_ignored()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseHawkRequestCorrelation();
        string? seen = null;
        app.Run(c => { seen = c.TraceIdentifier; return Task.CompletedTask; });
        var pipeline = app.Build();

        var valid = new DefaultHttpContext();
        valid.Request.Headers[RequestCorrelation.HeaderName] = "4f1c2a0e-7b3d-4e59-9c3a-1d2e3f405162";
        await pipeline(valid);
        Assert.Equal("4f1c2a0e-7b3d-4e59-9c3a-1d2e3f405162", seen);

        var invalid = new DefaultHttpContext { TraceIdentifier = "own" };
        invalid.Request.Headers[RequestCorrelation.HeaderName] = "bad id\r\nInjected: 1";
        await pipeline(invalid);
        Assert.Equal("own", seen);
    }

    [Theory]
    [InlineData(DatabaseProvider.Postgres)]
    [InlineData(DatabaseProvider.SqlServer)]
    public void Db_context_has_the_slow_sql_interceptor(DatabaseProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddHawkDbContext(provider, provider == DatabaseProvider.Postgres ? "Host=localhost;Database=x" : "Server=localhost;Database=x");
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        // Read the options without creating a context: building a model here (without the Identity options)
        // would be cached by EF and break other tests' model checks.
        var core = scope.ServiceProvider.GetRequiredService<DbContextOptions>().FindExtension<CoreOptionsExtension>();
        Assert.Contains(core!.Interceptors!, i => i is SlowSqlInterceptor);
    }

    [Theory]
    [InlineData(500, 501, true)]
    [InlineData(500, 500, false)]
    [InlineData(0, 100000, false)]
    public void Slow_sql_threshold(int thresholdMs, int durationMs, bool expected) =>
        Assert.Equal(expected, new SlowSqlInterceptor(NullLogger<SlowSqlInterceptor>.Instance, thresholdMs).IsSlow(TimeSpan.FromMilliseconds(durationMs)));
}
