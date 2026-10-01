using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class ExerciseConfiguration : IEntityTypeConfiguration<Exercise>
{
    public void Configure(EntityTypeBuilder<Exercise> builder)
    {
        builder.ToTable("exercises", ApplicationDbContext.DefaultSchema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.Purpose).HasMaxLength(200);
        builder.Property(e => e.Precautions).HasMaxLength(4000);
        builder.Property(e => e.MediaUrl).HasMaxLength(2048);
        builder.Property(e => e.MediaCaption).HasMaxLength(4000);
        builder.HasIndex(e => e.OwnerProfileId);
        builder.HasIndex(e => e.Name);
    }
}

internal sealed class TrainingPlanConfiguration : IEntityTypeConfiguration<TrainingPlan>
{
    public void Configure(EntityTypeBuilder<TrainingPlan> builder)
    {
        builder.ToTable("training_plans", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.ComplexProperty(p => p.Details, PlanDetailsMapping.Configure);
        builder.Property(p => p.Draft).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(p => new { p.TrainerProfileId, p.ClientId });
        builder.HasIndex(p => new { p.ClientId, p.Status });
    }
}

internal sealed class TrainingPlanVersionConfiguration : IEntityTypeConfiguration<TrainingPlanVersion>
{
    public void Configure(EntityTypeBuilder<TrainingPlanVersion> builder)
    {
        builder.ToTable("training_plan_versions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.ComplexProperty(v => v.Details, PlanDetailsMapping.Configure);
        builder.Property(v => v.Content).HasJsonConversion();
        builder.Property(v => v.ChangeDescription).HasMaxLength(1000).IsRequired();
        builder.HasIndex(v => new { v.TrainingPlanId, v.VersionNumber }).IsUnique();
        builder.HasOne<TrainingPlan>().WithMany().HasForeignKey(v => v.TrainingPlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class PlanDetailsMapping
{
    public static void Configure(ComplexPropertyBuilder<PlanDetails> details)
    {
        details.Property(d => d.Name).HasMaxLength(200).HasColumnName("name");
        details.Property(d => d.Goal).HasMaxLength(500).HasColumnName("goal");
        details.Property(d => d.Level).HasColumnName("level");
        details.Property(d => d.StartDate).HasColumnName("start_date");
        details.Property(d => d.EndDate).HasColumnName("end_date");
        details.Property(d => d.SuggestedSessionsPerWeek).HasColumnName("suggested_sessions_per_week");
        details.Property(d => d.Notes).HasMaxLength(4000).HasColumnName("notes");
    }
}

internal sealed class WorkoutSessionConfiguration : IEntityTypeConfiguration<WorkoutSession>
{
    public void Configure(EntityTypeBuilder<WorkoutSession> builder)
    {
        builder.ToTable("workout_sessions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PlannedSessionName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property<uint>("version").IsRowVersion();
        builder.Ignore(s => s.EndedAt);
        builder.Ignore(s => s.IsOpen);

        builder.OwnsOne(s => s.Feedback, feedback =>
        {
            feedback.Property(f => f.PerceivedExertion).HasColumnName("feedback_perceived_exertion");
            feedback.Property(f => f.ReportedDiscomfort).HasColumnName("feedback_reported_discomfort");
            feedback.Property(f => f.DiscomfortDetails).HasMaxLength(2000).HasColumnName("feedback_discomfort_details");
            feedback.Property(f => f.Comments).HasMaxLength(2000).HasColumnName("feedback_comments");
        });
        builder.OwnsOne(s => s.Reply, reply =>
        {
            reply.Property(r => r.Message).HasMaxLength(2000).HasColumnName("reply_message");
            reply.Property(r => r.RepliedByProfileId).HasColumnName("reply_replied_by_profile_id");
            reply.Property(r => r.RepliedAt).HasColumnName("reply_replied_at");
        });
        builder.OwnsOne(s => s.AppleHealthWorkout, export =>
        {
            export.Property(e => e.ExternalWorkoutId).HasColumnName("apple_health_workout_id");
            export.Property(e => e.SavedAt).HasColumnName("apple_health_workout_saved_at");
            export.Property(e => e.RecordedAt).HasColumnName("apple_health_workout_recorded_at");
        });

        builder.HasMany(s => s.Sets).WithOne().HasForeignKey("workout_session_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Sets).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(s => s.Pauses).WithOne().HasForeignKey("workout_session_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Pauses).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasOne<TrainingPlanVersion>().WithMany().HasForeignKey(s => s.PlanVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.ClientId, s.Status, s.LocalDate });
        builder.HasIndex(s => new { s.TrainerProfileId, s.ClientId });
        builder.HasIndex(s => s.TrainingPlanId);
    }
}

internal sealed class SetLogConfiguration : IEntityTypeConfiguration<SetLog>
{
    public void Configure(EntityTypeBuilder<SetLog> builder)
    {
        builder.ToTable("workout_set_logs", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.LoadKg).HasPrecision(7, 2);
        builder.Property(s => s.DistanceMeters).HasPrecision(10, 2);
        builder.Property(s => s.Notes).HasMaxLength(1000);
        builder.Ignore(s => s.Volume);
        builder.HasIndex(s => s.ExerciseId);
    }
}

internal sealed class WorkoutPauseConfiguration : IEntityTypeConfiguration<WorkoutPause>
{
    public void Configure(EntityTypeBuilder<WorkoutPause> builder)
    {
        builder.ToTable("workout_pauses", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
    }
}
