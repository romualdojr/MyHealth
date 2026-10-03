using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Privacy;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class PrivacyRequestConfiguration : IEntityTypeConfiguration<PrivacyRequest>
{
    public void Configure(EntityTypeBuilder<PrivacyRequest> builder)
    {
        builder.ToTable("privacy_requests", ApplicationDbContext.DefaultSchema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Details).HasMaxLength(4000);
        builder.Property(r => r.ResolutionNotes).HasMaxLength(4000);
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(r => new { r.UserId, r.SubmittedAt });
        builder.HasIndex(r => new { r.Status, r.Type });
    }
}

internal sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> builder)
    {
        builder.ToTable("retention_policies", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.RecordType).HasMaxLength(64).IsRequired();
        builder.Property(p => p.LegalBasis).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(2000);
        builder.HasIndex(p => p.RecordType).IsUnique();
    }
}

internal sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    public void Configure(EntityTypeBuilder<PushDevice> builder)
    {
        builder.ToTable("push_devices", ApplicationDbContext.DefaultSchema);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Token).HasMaxLength(4096).IsRequired();
        builder.HasIndex(d => d.InstallationId).IsUnique();
        builder.HasIndex(d => d.UserId);
    }
}
