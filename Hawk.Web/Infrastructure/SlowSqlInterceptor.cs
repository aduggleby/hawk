// <file>
// <summary>
// Logs EF Core commands slower than Hawk:Logging:SlowSqlMs (default 500, 0 = off) at Warning with their SQL text.
// </summary>
// </file>

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hawk.Web.Infrastructure;

/// <summary>
/// Warns about slow EF commands (queries until the first row is available, and non-queries such as
/// ExecuteUpdate/ExecuteDelete). Logs the parameterized SQL only, never parameter values.
/// Hangfire's own storage queries do not go through EF and are not covered.
/// </summary>
public sealed class SlowSqlInterceptor(ILogger<SlowSqlInterceptor> logger, int thresholdMs) : DbCommandInterceptor
{
    public const int DefaultThresholdMs = 500;

    public static SlowSqlInterceptor Create(IServiceProvider services)
    {
        // Optional services: some hosts (tests) register the context without configuration or logging.
        var threshold = Math.Max(0, services.GetService<IConfiguration>()?.GetValue("Hawk:Logging:SlowSqlMs", DefaultThresholdMs) ?? DefaultThresholdMs);
        var logger = services.GetService<ILogger<SlowSqlInterceptor>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SlowSqlInterceptor>.Instance;
        return new SlowSqlInterceptor(logger, threshold);
    }

    public bool IsSlow(TimeSpan duration) => thresholdMs > 0 && duration.TotalMilliseconds > thresholdMs;

    private void Check(DbCommand command, CommandExecutedEventData data)
    {
        if (IsSlow(data.Duration))
            logger.LogWarning("Slow SQL ({ElapsedMs:0} ms, {Context}): {Sql}",
                data.Duration.TotalMilliseconds, data.Context?.GetType().Name ?? "-", command.CommandText);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }
}
