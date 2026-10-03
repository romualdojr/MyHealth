using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Tenants;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class HealthHistoryConfiguration : IEntityTypeConfiguration<HealthHistory>
{
    public void Configure(EntityTypeBuilder<HealthHistory> builder)
    {
        builder.ToTable("health_histories", ApplicationDbContext.DefaultSchema);
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property<uint>("version").IsRowVersion();
        builder.Ignore(h => h.Current);

        // One history per client, created on first submission; a concurrent first submission fails with 409 and is retried.
        builder.HasIndex(h => h.ClientId).IsUnique();

        builder.HasMany(h => h.Revisions).WithOne().HasForeignKey("health_history_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(h => h.Revisions).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(h => h.Reviews).WithOne().HasForeignKey("health_history_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(h => h.Reviews).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class HealthHistoryRevisionConfiguration : IEntityTypeConfiguration<HealthHistoryRevision>
{
    public void Configure(EntityTypeBuilder<HealthHistoryRevision> builder)
    {
        builder.ToTable("health_history_revisions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Content).HasJsonConversion();
        builder.Property<Guid>("health_history_id");
        builder.HasIndex("health_history_id", nameof(HealthHistoryRevision.Number)).IsUnique();
    }
}

internal sealed class HealthHistoryReviewConfiguration : IEntityTypeConfiguration<HealthHistoryReview>
{
    public void Configure(EntityTypeBuilder<HealthHistoryReview> builder)
    {
        builder.ToTable("health_history_reviews", ApplicationDbContext.DefaultSchema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Notes).HasMaxLength(2000);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.TenantId);
    }
}

internal sealed class PhysicalAssessmentConfiguration : IEntityTypeConfiguration<PhysicalAssessment>
{
    public void Configure(EntityTypeBuilder<PhysicalAssessment> builder)
    {
        builder.ToTable("physical_assessments", ApplicationDbContext.DefaultSchema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Measurements).HasJsonConversion();
        builder.Property(a => a.Corrections).HasJsonConversion();
        builder.Property(a => a.Notes).HasMaxLength(4000);
        // The domain already has a Version (correction count), so the concurrency token is named after the system column.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.ClientId });
        builder.HasIndex(a => new { a.ClientId, a.AssessedOn });
    }
}
