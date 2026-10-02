using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Assistant;
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
