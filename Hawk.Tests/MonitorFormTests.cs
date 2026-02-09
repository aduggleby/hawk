// <file>
// <summary>
// Unit tests for monitor form validation and interval rules.
// </summary>
// </file>

using Hawk.Web.Pages.Monitors;
using Hawk.Web.Services.Monitoring;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hawk.Tests;

/// <summary>
/// Tests for <see cref="MonitorForm"/>.
/// </summary>
public class MonitorFormTests
{
    [Fact]
    public void AllowedIntervals_Testing_IncludesFiveSeconds()
    {
        var env = new FakeEnv("Testing");
        var allowed = MonitorIntervals.AllowedSeconds(env);
        Assert.Contains(5, allowed);
    }

    [Fact]
    public void AllowedIntervals_Production_DoesNotIncludeFiveSeconds()
    {
        var env = new FakeEnv("Production");
        var allowed = MonitorIntervals.AllowedSeconds(env);
        Assert.DoesNotContain(5, allowed);
    }

    [Fact]
    public void Validate_Rejects_NonHttpUrl()
    {
        var env = new FakeEnv("Testing");
        var form = new MonitorForm
        {
            Name = "x",
            Url = "ftp://example.com",
            Method = "GET",
            IntervalSeconds = 60
        };
        var errors = form.Validate(env).ToArray();
        Assert.Contains(errors, e => e.ErrorMessage?.Contains("http/https", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Validate_Rejects_IntervalNotAllowed()
    {
        var env = new FakeEnv("Production");
        var form = new MonitorForm
        {
            Name = "x",
            Url = "https://example.com",
            Method = "GET",
            IntervalSeconds = 5
        };
        var errors = form.Validate(env).ToArray();
        Assert.Contains(errors, e => e.ErrorMessage?.Contains("Interval", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class FakeEnv(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "Hawk.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

