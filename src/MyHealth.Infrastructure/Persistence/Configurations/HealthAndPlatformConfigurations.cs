using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class HealthConnectionConfiguration : IEntityTypeConfiguration<HealthConnection>
{
    public void Configure(EntityTypeBuilder<HealthConnection> builder)
    {
        builder.ToTable("health_connections", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Ignore(c => c.AllowsImport);
        builder.Ignore(c => c.AllowsSharing);
        builder.HasIndex(c => new { c.ClientId, c.Provider }).IsUnique();

        builder.HasMany(c => c.History).WithOne().HasForeignKey("health_connection_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.History).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class HealthConnectionChangeConfiguration : IEntityTypeConfiguration<HealthConnectionChange>
{
    public void Configure(EntityTypeBuilder<HealthConnectionChange> builder)
    {
        builder.ToTable("health_connection_changes", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
    }
}

internal sealed class WorkoutHeartRateConfiguration : IEntityTypeConfiguration<WorkoutHeartRate>
{
    public void Configure(EntityTypeBuilder<WorkoutHeartRate> builder)
    {
        builder.ToTable("workout_heart_rates", ApplicationDbContext.DefaultSchema);
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property<uint>("version").IsRowVersion();

        // Same id as the workout session: one heart-rate record per session.
        builder.HasOne<WorkoutSession>().WithOne().HasForeignKey<WorkoutHeartRate>(h => h.Id).OnDelete(DeleteBehavior.Restrict);

        builder.OwnsOne(h => h.Summary, summary =>
        {
            summary.Property(s => s.SampleCount).HasColumnName("summary_sample_count");
            summary.Property(s => s.SelfReportedSampleCount).HasColumnName("summary_self_reported_sample_count");
            summary.Property(s => s.AverageBeatsPerMinute).HasColumnName("summary_average_bpm");
            summary.Property(s => s.TimeWeightedAverageBeatsPerMinute).HasColumnName("summary_time_weighted_average_bpm");
            summary.Property(s => s.TimeWeightedMethod).HasMaxLength(64).HasColumnName("summary_time_weighted_method");
            summary.Property(s => s.MaximumBeatsPerMinute).HasColumnName("summary_maximum_bpm");
            summary.Property(s => s.MinimumBeatsPerMinute).HasColumnName("summary_minimum_bpm");
            summary.Property(s => s.FirstSampleAt).HasColumnName("summary_first_sample_at");
            summary.Property(s => s.LastSampleAt).HasColumnName("summary_last_sample_at");
            summary.Property(s => s.Method).HasMaxLength(64).HasColumnName("summary_method");
            summary.Property(s => s.CalculatedAt).HasColumnName("summary_calculated_at");
        });

        builder.HasMany(h => h.Samples).WithOne().HasForeignKey("workout_heart_rate_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(h => h.Samples).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(h => h.Syncs).WithOne().HasForeignKey("workout_heart_rate_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(h => h.Syncs).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(h => h.ClientId);
    }
}

internal sealed class HeartRateSampleConfiguration : IEntityTypeConfiguration<HeartRateSample>
{
    public void Configure(EntityTypeBuilder<HeartRateSample> builder)
    {
        builder.ToTable("heart_rate_samples", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.SourceName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.DeviceName).HasMaxLength(200);
        builder.Ignore(s => s.IsActive);

        // Database-level guarantee of the README contract: a HealthKit sample is stored once per client, whatever the
        // retries or overlapping sessions. A concurrent duplicate insert fails with 409 and the phone retries.
        builder.Property<Guid>("workout_heart_rate_id");
        builder.HasIndex(s => new { s.ClientId, s.ExternalId }).IsUnique();
        builder.HasIndex("workout_heart_rate_id");
    }
}

internal sealed class HeartRateSyncConfiguration : IEntityTypeConfiguration<HeartRateSync>
{
    public void Configure(EntityTypeBuilder<HeartRateSync> builder)
    {
        builder.ToTable("heart_rate_syncs", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", ApplicationDbContext.DefaultSchema);
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.ResourceType).HasMaxLength(100).IsRequired();
        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries", ApplicationDbContext.DefaultSchema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ResourceType).HasMaxLength(100).IsRequired();
        builder.HasIndex(a => new { a.SubjectUserId, a.OccurredAt });
        builder.HasIndex(a => new { a.ActorUserId, a.OccurredAt });
    }
}
