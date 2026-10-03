using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class TherapyModalityConfiguration : IEntityTypeConfiguration<TherapyModality>
{
    public void Configure(EntityTypeBuilder<TherapyModality> builder)
    {
        builder.ToTable("therapy_modalities", ApplicationDbContext.DefaultSchema);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Code).HasMaxLength(64).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Description).HasMaxLength(2000);
        builder.Property(m => m.LegalReviewReference).HasMaxLength(500);
        builder.Property<uint>("version").IsRowVersion();
        builder.HasIndex(m => m.Code).IsUnique();
    }
}

internal sealed class PractitionerQualificationConfiguration : IEntityTypeConfiguration<PractitionerQualification>
{
    public void Configure(EntityTypeBuilder<PractitionerQualification> builder)
    {
        builder.ToTable("practitioner_qualifications", ApplicationDbContext.DefaultSchema);
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.Title).HasMaxLength(200).IsRequired();
        builder.Property(q => q.Issuer).HasMaxLength(200).IsRequired();
        builder.Property(q => q.CertificateNumber).HasMaxLength(100);
        builder.Property(q => q.ReviewNotes).HasMaxLength(1000);
        builder.HasOne<TherapyModality>().WithMany().HasForeignKey(q => q.ModalityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(q => new { q.ProfessionalProfileId, q.ModalityId, q.SubmittedAt });
        builder.HasIndex(q => q.Status);
    }
}

internal sealed class TherapyServiceConfiguration : IEntityTypeConfiguration<TherapyService>
{
    public void Configure(EntityTypeBuilder<TherapyService> builder)
    {
        builder.ToTable("therapy_services", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TherapyModality>().WithMany().HasForeignKey(s => s.ModalityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.PractitionerProfileId });
    }
}

internal sealed class ConsentTemplateConfiguration : IEntityTypeConfiguration<ConsentTemplate>
{
    public void Configure(EntityTypeBuilder<ConsentTemplate> builder)
    {
        builder.ToTable("therapy_consent_templates", ApplicationDbContext.DefaultSchema);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Title).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(20_000).IsRequired();
        builder.HasOne<TherapyModality>().WithMany().HasForeignKey(t => t.ModalityId).OnDelete(DeleteBehavior.Restrict);

        // Two operators publishing at once cannot both create the same version.
        builder.HasIndex(t => new { t.ModalityId, t.Version }).IsUnique();
    }
}

internal sealed class TherapyConsentConfiguration : IEntityTypeConfiguration<TherapyConsent>
{
    public void Configure(EntityTypeBuilder<TherapyConsent> builder)
    {
        builder.ToTable("therapy_consents", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.WithdrawalReason).HasMaxLength(1000);
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConsentTemplate>().WithMany().HasForeignKey(c => c.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.ClientId, c.TenantId, c.PractitionerProfileId, c.ModalityId, c.DecidedAt });
    }
}

internal sealed class TherapySessionConfiguration : IEntityTypeConfiguration<TherapySession>
{
    public void Configure(EntityTypeBuilder<TherapySession> builder)
    {
        builder.ToTable("therapy_sessions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.ServiceName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.CancellationReason).HasMaxLength(1000);
        builder.Property(s => s.Record).HasJsonConversion();
        builder.Property(s => s.Corrections).HasJsonConversion();
        builder.Property(s => s.ClientReport).HasJsonConversion();
        builder.Property(s => s.Referrals).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TherapyService>().WithMany().HasForeignKey(s => s.ServiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.PractitionerProfileId, s.Status, s.StartsAt });
        builder.HasIndex(s => new { s.TenantId, s.ClientId });
        builder.HasIndex(s => s.ClientId);
    }
}

internal sealed class TherapyCarePlanConfiguration : IEntityTypeConfiguration<TherapyCarePlan>
{
    public void Configure(EntityTypeBuilder<TherapyCarePlan> builder)
    {
        builder.ToTable("therapy_care_plans", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Goals).HasMaxLength(4000).IsRequired();
        builder.Property(p => p.Frequency).HasMaxLength(200);
        builder.Property(p => p.Reviews).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TherapyModality>().WithMany().HasForeignKey(p => p.ModalityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.PractitionerProfileId, p.ClientId });
        builder.HasIndex(p => p.ClientId);
    }
}
