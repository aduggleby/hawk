// <file>
// <summary>
// MonitorCommands.QueueManualRunAsync must not leave a queued run behind when the Hangfire enqueue fails.
// </summary>
// </file>

using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hawk.Web.Data;
using Hawk.Web.Services.Monitoring;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Tests;

public sealed class MonitorCommandsEnqueueTests
{
    [Fact]
    public async Task Failed_enqueue_removes_the_queued_run_and_rethrows()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("n")).Options;
        await using var db = new ApplicationDbContext(opts);
        var monitor = new MonitorEntity { Name = "m", Url = "https://example.com", Method = "GET", IntervalSeconds = 60 };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync();

        var commands = new MonitorCommands(db, new ThrowingJobs());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.QueueManualRunAsync(monitor.Id, reuseInFlight: true, rejectDisabled: true, CancellationToken.None));

        Assert.Empty(await db.MonitorRuns.AsNoTracking().ToListAsync());
        // The next call is not answered with a phantom in-flight run.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.QueueManualRunAsync(monitor.Id, reuseInFlight: true, rejectDisabled: true, CancellationToken.None));
    }

    private sealed class ThrowingJobs : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => throw new InvalidOperationException("storage down");
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
