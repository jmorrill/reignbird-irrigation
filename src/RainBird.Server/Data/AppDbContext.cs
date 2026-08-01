using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RainBird.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ControllerRecord> Controllers => Set<ControllerRecord>();
    public DbSet<ZoneRecord> Zones => Set<ZoneRecord>();
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<SkipEventRecord> SkipEvents => Set<SkipEventRecord>();
    public DbSet<WeatherDayRecord> WeatherDays => Set<WeatherDayRecord>();
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    public DbSet<WateringPlan> WateringPlans => Set<WateringPlan>();
    public DbSet<PlanZone> PlanZones => Set<PlanZone>();
    public DbSet<PlanRun> PlanRuns => Set<PlanRun>();
    public DbSet<PlanRunStep> PlanRunSteps => Set<PlanRunStep>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ControllerRecord>()
            .HasMany(c => c.Zones)
            .WithOne(z => z.Controller!)
            .HasForeignKey(z => z.ControllerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ZoneRecord>()
            .HasIndex(z => new { z.ControllerId, z.StationNumber })
            .IsUnique();

        // History is always queried as "this controller, this date range".
        builder.Entity<RunRecord>()
            .HasIndex(r => new { r.ControllerId, r.StartedUtc });

        builder.Entity<WeatherDayRecord>()
            .HasIndex(w => new { w.ControllerId, w.Date })
            .IsUnique();

        builder.Entity<SkipEventRecord>()
            .HasIndex(s => new { s.ControllerId, s.Date });

        builder.Entity<SettingRecord>().HasKey(s => s.Key);

        // Usernames are stored as typed but matched without regard to case, so
        // "Sam" cannot be registered alongside "sam" and then be impossible to tell
        // apart at the login prompt.
        builder.Entity<UserRecord>()
            .HasIndex(u => u.Username)
            .IsUnique();

        builder.Entity<UserRecord>()
            .Property(u => u.Username)
            .UseCollation("NOCASE");

        // The alert list is always "most recent first", and always short.
        builder.Entity<AlertRecord>().HasIndex(a => a.CreatedUtc);

        // The endpoint identifies the device; re-subscribing must update rather than
        // duplicate, or one phone would get the same notification several times.
        builder.Entity<PushSubscriptionRecord>().HasIndex(p => p.Endpoint).IsUnique();

        builder.Entity<WateringPlan>()
            .HasMany(plan => plan.Zones)
            .WithOne(zone => zone.Plan!)
            .HasForeignKey(zone => zone.WateringPlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PlanRun>()
            .HasMany(run => run.Steps)
            .WithOne(step => step.Run!)
            .HasForeignKey(step => step.PlanRunId)
            .OnDelete(DeleteBehavior.Cascade);

        // "Has this pass already run?" is checked on every scheduler tick, and the
        // uniqueness is what stops a restart re-firing a pass.
        builder.Entity<PlanRun>()
            .HasIndex(run => new { run.WateringPlanId, run.ScheduledDate, run.ScheduledStartMinute })
            .IsUnique();

        builder.Entity<PlanRun>().HasIndex(run => new { run.ControllerId, run.StartedUtc });
        builder.Entity<WateringPlan>().HasIndex(plan => plan.ControllerId);
    }
}

/// <summary>
/// SQLite has no native date type and cannot order or compare a
/// <see cref="DateTimeOffset"/> in SQL. Storing UTC ticks as an integer keeps range
/// filters and ORDER BY in the database, which matters because the run history is
/// the largest table and is always queried by date range.
/// </summary>
public sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero)) { }
}

/// <inheritdoc cref="UtcTicksConverter"/>
public sealed class NullableUtcTicksConverter : ValueConverter<DateTimeOffset?, long?>
{
    public NullableUtcTicksConverter()
        : base(
            value => value == null ? null : value.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero)) { }
}
