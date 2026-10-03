using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Users;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles", ApplicationDbContext.DefaultSchema);
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.Property(u => u.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

internal sealed class ProfessionalProfileConfiguration : IEntityTypeConfiguration<ProfessionalProfile>
{
    public void Configure(EntityTypeBuilder<ProfessionalProfile> builder)
    {
        builder.ToTable("professional_profiles", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Bio).HasMaxLength(2000);
        builder.Property(p => p.PhotoUrl).HasMaxLength(2048);
        builder.Property(p => p.ReviewNotes).HasMaxLength(1000);
        builder.ComplexProperty(p => p.Registration, registration =>
        {
            registration.Property(r => r.Council).HasMaxLength(20).HasColumnName("registration_council");
            registration.Property(r => r.Number).HasMaxLength(30).HasColumnName("registration_number");
            registration.Property(r => r.Region).HasMaxLength(10).HasColumnName("registration_region");
        });
        builder.Ignore(p => p.IsVerified);
        builder.HasIndex(p => new { p.UserId, p.Discipline }).IsUnique();
        builder.HasIndex(p => p.VerificationStatus);
    }
}

internal sealed class CareRelationshipConfiguration : IEntityTypeConfiguration<CareRelationship>
{
    public void Configure(EntityTypeBuilder<CareRelationship> builder)
    {
        builder.ToTable("care_relationships", ApplicationDbContext.DefaultSchema);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.InviteeEmail).HasMaxLength(320).IsRequired();
        builder.Property(r => r.EndReason).HasMaxLength(1000);
        builder.Property<uint>("version").IsRowVersion();
        builder.Ignore(r => r.IsActive);

        builder.HasMany(r => r.Grants).WithOne().HasForeignKey("care_relationship_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Grants).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.ProfessionalUserId, r.Discipline, r.ClientId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.ProfessionalProfileId, r.InviteeEmail, r.Status });
        builder.HasIndex(r => new { r.ClientId, r.Status });
    }
}

internal sealed class DataSharingGrantConfiguration : IEntityTypeConfiguration<DataSharingGrant>
{
    public void Configure(EntityTypeBuilder<DataSharingGrant> builder)
    {
        builder.ToTable("data_sharing_grants", ApplicationDbContext.DefaultSchema);
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Ignore(g => g.IsActive);
    }
}
