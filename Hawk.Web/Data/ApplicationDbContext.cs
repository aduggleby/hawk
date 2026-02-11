// <file>
// <summary>
// EF Core DbContext for the Hawk web application. Currently hosts ASP.NET Core Identity tables and will also host
// monitoring configuration and check history once those features are implemented.
// </summary>
// </file>

﻿using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.UrlChecks;

namespace Hawk.Web.Data;

/// <summary>
/// Application EF Core DbContext (Identity + app data).
/// </summary>
/// <param name="options">DbContext options.</param>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
    /// <summary>
    /// Configured monitors.
    /// </summary>
    public DbSet<MonitorEntity> Monitors => Set<MonitorEntity>();

    /// <summary>
    /// Per-monitor headers.
    /// </summary>
    public DbSet<MonitorHeader> MonitorHeaders => Set<MonitorHeader>();

    /// <summary>
    /// Per-monitor match rules.
    /// </summary>
    public DbSet<MonitorMatchRule> MonitorMatchRules => Set<MonitorMatchRule>();

    /// <summary>
    /// Monitor execution history.
    /// </summary>
    public DbSet<MonitorRun> MonitorRuns => Set<MonitorRun>();

    /// <summary>
    /// Per-user alerting settings (alert recipient email override).
    /// </summary>
    public DbSet<UserAlertSettings> UserAlertSettings => Set<UserAlertSettings>();

    /// <summary>
    /// Per-user HTTP settings for URL checks (e.g., User-Agent override).
    /// </summary>
    public DbSet<UserUrlCheckSettings> UserUrlCheckSettings => Set<UserUrlCheckSettings>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<MonitorEntity>(b =>
        {
            b.HasIndex(x => x.Enabled);
            b.HasIndex(x => x.NextRunAt);
            b.HasMany(x => x.Headers).WithOne(x => x.Monitor!).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.MatchRules).WithOne(x => x.Monitor!).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MonitorRun>(b =>
        {
            b.HasIndex(x => new { x.MonitorId, x.StartedAt });
        });

        builder.Entity<UserAlertSettings>(b =>
        {
            b.HasKey(x => x.UserId);
            b.HasIndex(x => x.AlertEmail);
        });

        builder.Entity<UserUrlCheckSettings>(b =>
        {
            b.HasKey(x => x.UserId);
        });
    }
}
