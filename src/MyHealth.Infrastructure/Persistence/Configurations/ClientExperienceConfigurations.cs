using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Messaging;
using MyHealth.Domain.Wellbeing;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class WellbeingCheckInConfiguration : IEntityTypeConfiguration<WellbeingCheckIn>
{
    public void Configure(EntityTypeBuilder<WellbeingCheckIn> builder)
    {
        builder.ToTable("wellbeing_check_ins", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(c => new { c.ClientId, c.LocalDate }).IsUnique();
    }
}

internal sealed class AssistantConsentConfiguration : IEntityTypeConfiguration<AssistantConsent>
{
    public void Configure(EntityTypeBuilder<AssistantConsent> builder)
    {
        builder.ToTable("assistant_consents", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.History).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(c => c.UserId).IsUnique();
    }
}

internal sealed class AssistantProposalConfiguration : IEntityTypeConfiguration<AssistantProposal>
{
    public void Configure(EntityTypeBuilder<AssistantProposal> builder)
    {
        builder.ToTable("assistant_proposals", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(p => new { p.UserId, p.CreatedAt });
    }
}

internal sealed class CoachAnalysisConfiguration : IEntityTypeConfiguration<CoachAnalysis>
{
    public void Configure(EntityTypeBuilder<CoachAnalysis> builder)
    {
        builder.ToTable("coach_analyses", ApplicationDbContext.DefaultSchema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Model).HasMaxLength(100);
        builder.Property(a => a.Focus).HasMaxLength(500);
        builder.Property(a => a.Summary).HasMaxLength(2000);
        builder.Property(a => a.Notices).HasJsonConversion();
        builder.Property(a => a.Suggestions).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Domain.Tenants.Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TrainingPlanId, a.CreatedAt });
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt });
    }
}

internal sealed class WhatsAppContactConfiguration : IEntityTypeConfiguration<WhatsAppContact>
{
    public void Configure(EntityTypeBuilder<WhatsAppContact> builder)
    {
        builder.ToTable("whatsapp_contacts", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.PhoneNumber).HasMaxLength(16);
        builder.Property(c => c.VerificationCodeHash).HasMaxLength(64);
        builder.Property(c => c.History).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(c => c.UserId).IsUnique();
        builder.HasIndex(c => c.PhoneNumber);
    }
}
