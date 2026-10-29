// <file>
// <summary>
// Tests for the run diagnostics alert wording.
// </summary>
// </file>

using Hawk.Web.Pages.Monitors.Runs;
using Xunit;

namespace Hawk.Tests;

public sealed class RunAlertStatusTests
{
    [Theory]
    [InlineData("Not alerted (failures=51, threshold=2).", "Not sent for this run (failure 51 of an incident; alert threshold 2)")]
    [InlineData("No failure alert delivered yet (failures=3, threshold=2).", "No failure alert delivered yet (failure 3 of an incident; alert threshold 2)")]
    [InlineData("Email disabled (Hawk:Email:Enabled=false).", "Email disabled (Hawk:Email:Enabled=false).")]
    [InlineData(null, "not sent")]
    [InlineData("  ", "not sent")]
    public void DescribeAlertError_uses_words(string? input, string expected) =>
        Assert.Equal(expected, DetailsModel.DescribeAlertError(input));
}
