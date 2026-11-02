using Hawk.Web.Data;
using Hawk.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Hawk.Tests;

public class HealthRequestAbortTests
{
    [Fact]
    public async Task Aborted_ready_probe_ends_as_499_and_is_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var context = NewContext("/ready", cts.Token);
        var pipeline = Pipeline(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await pipeline(context);

        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal(LogEventLevel.Verbose, HealthRequestAbort.GetLevel(context, ex: null));
    }

    [Fact]
    public async Task Aborted_probe_without_exception_is_499()
    {
        using var cts = new CancellationTokenSource();
        var context = NewContext("/live", cts.Token);
        var pipeline = Pipeline(_ =>
        {
            cts.Cancel();
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.Equal(499, context.Response.StatusCode);
    }

    [Fact]
    public async Task Cancellation_without_a_client_abort_still_fails()
    {
        var context = NewContext("/ready", CancellationToken.None);
        var pipeline = Pipeline(_ => throw new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline(context));
    }

    [Fact]
    public async Task Unhealthy_probe_keeps_503_and_logs_at_error()
    {
        var context = NewContext("/ready", CancellationToken.None);
        var pipeline = Pipeline(c =>
        {
            c.Response.StatusCode = 503;
            return Task.CompletedTask;
        });

        await pipeline(context);

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(LogEventLevel.Error, HealthRequestAbort.GetLevel(context, ex: null));
    }

    [Fact]
    public async Task Database_check_is_not_unhealthy_when_the_probe_is_aborted()
    {
        await using var provider = HealthProvider([]);
        using var scope = provider.CreateScope();
        var check = new DatabaseHealthCheck(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Database_check_is_unhealthy_and_logs_at_error_when_the_database_is_down()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        await using var provider = HealthProvider(logs);

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Contains(logs, l => l.Level == LogLevel.Error);
    }

    private static RequestDelegate Pipeline(RequestDelegate terminal)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseHawkHealthRequestAbort();
        app.Run(terminal);
        return app.Build();
    }

    private static DefaultHttpContext NewContext(string path, CancellationToken aborted)
    {
        var context = new DefaultHttpContext { RequestAborted = aborted };
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        return context;
    }

    private static ServiceProvider HealthProvider(List<(LogLevel Level, string Message)> logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new ListLoggerProvider(logs)));
        // Nothing listens on port 1: the connection attempt fails fast.
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2;Pooling=false"));
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [DatabaseHealthCheck.ReadyTag]);
        return services.BuildServiceProvider();
    }

    private sealed class ListLoggerProvider(List<(LogLevel, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(logs);
        public void Dispose() { }

        private sealed class ListLogger(List<(LogLevel, string)> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (logs) logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
