// <file>
// <summary>
// EF Core DbContext for the Hawk web application (Identity + monitoring configuration and history).
// This type is also the SQL Server context: its migrations live in Data/Migrations and keep the existing
// __EFMigrationsHistory. PostgreSQL uses the PostgresApplicationDbContext subclass with its own migration set.
// The model is provider-agnostic: no column types or provider-specific features are configured here.
// </summary>
// </file>

﻿using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;
using Hawk.Web.Data.Monitoring;
using Hawk.Web.Data.Alerting;
using Hawk.Web.Data.ApiKeys;
using Microsoft.AspNetCore.Identity;
using Hawk.Web.Data.UrlChecks;

namespace Hawk.Web.Data;

/// <summary>
/// Application EF Core DbContext (Identity + app data).
/// </summary>
public class ApplicationDbContext : IdentityDbContext
{
    /// <summary>
    /// Creates the SQL Server (default) context.
    /// </summary>
    /// <param name="options">DbContext options.</param>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Used by provider-specific subclasses (see <see cref="PostgresApplicationDbContext"/>).
    /// </summary>
    /// <param name="options">DbContext options for the derived context type.</param>
    protected ApplicationDbContext(DbContextOptions options) : base(options)
    {
    }

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
    /// 15-minute rollup of completed runs (see RunBucketRollup).
    /// </summary>
    public DbSet<MonitorRunBucket> MonitorRunBuckets => Set<MonitorRunBucket>();

    /// <summary>
    /// Per-user alerting settings (alert recipient email override).
    /// </summary>
    public DbSet<UserAlertSettings> UserAlertSettings => Set<UserAlertSettings>();

    /// <summary>
    /// Per-monitor alert state (for reminders and recovery emails).
    /// </summary>
    public DbSet<MonitorAlertState> MonitorAlertStates => Set<MonitorAlertState>();

    /// <summary>
    /// Per-user HTTP settings for URL checks (e.g., User-Agent override).
    /// </summary>
    public DbSet<UserUrlCheckSettings> UserUrlCheckSettings => Set<UserUrlCheckSettings>();

    /// <summary>
    /// Per-user monitor behavior settings (e.g., run retention).
    /// </summary>
    public DbSet<UserMonitorSettings> UserMonitorSettings => Set<UserMonitorSettings>();

    /// <summary>
    /// Per-user API keys (hashed).
    /// </summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<MonitorEntity>(b =>
        {
            b.HasIndex(x => x.Enabled);
            b.HasIndex(x => x.NextRunAt);
            b.HasIndex(x => x.HeartbeatToken).IsUnique();
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            b.HasMany(x => x.Headers).WithOne(x => x.Monitor!).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.MatchRules).WithOne(x => x.Monitor!).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MonitorRun>(b =>
        {
            b.HasIndex(x => new { x.MonitorId, x.StartedAt });
        });

        builder.Entity<MonitorRunBucket>(b =>
        {
            b.HasKey(x => new { x.MonitorId, x.BucketStart });
            b.HasOne<MonitorEntity>().WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
            // Cleanup and "newest bucket" lookups filter on BucketStart alone.
            b.HasIndex(x => x.BucketStart);
        });

        builder.Entity<UserAlertSettings>(b =>
        {
            b.HasKey(x => x.UserId);
            b.HasIndex(x => x.AlertEmail);
        });

        builder.Entity<MonitorAlertState>(b =>
        {
            b.HasKey(x => x.MonitorId);
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            b.HasOne(x => x.Monitor)
                .WithOne()
                .HasForeignKey<MonitorAlertState>(x => x.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => x.PendingRecoveryAlert);
        });

        builder.Entity<UserUrlCheckSettings>(b =>
        {
            b.HasKey(x => x.UserId);
        });

        builder.Entity<UserMonitorSettings>(b =>
        {
            b.HasKey(x => x.UserId);
        });

        builder.Entity<ApiKey>(b =>
        {
            b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => x.SecretHash).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
