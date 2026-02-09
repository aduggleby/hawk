// <file>
// <summary>
// Unit tests for alert policy logic.
// </summary>
// </file>

using Hawk.Web.Services.Monitoring;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="AlertPolicy"/>.
/// </summary>
public sealed class AlertPolicyTests
{
    [Fact]
    public void ShouldAlertOnFailure_threshold1_alerts_on_first_failure_only()
    {
        Assert.True(AlertPolicy.ShouldAlertOnFailure(1, priorConsecutiveFailures: 0));
        Assert.False(AlertPolicy.ShouldAlertOnFailure(1, priorConsecutiveFailures: 1));
        Assert.False(AlertPolicy.ShouldAlertOnFailure(1, priorConsecutiveFailures: 2));
    }

    [Fact]
    public void ShouldAlertOnFailure_threshold2_alerts_when_reaching_two()
    {
        Assert.False(AlertPolicy.ShouldAlertOnFailure(2, priorConsecutiveFailures: 0));
        Assert.True(AlertPolicy.ShouldAlertOnFailure(2, priorConsecutiveFailures: 1));
        Assert.False(AlertPolicy.ShouldAlertOnFailure(2, priorConsecutiveFailures: 2));
    }
}

