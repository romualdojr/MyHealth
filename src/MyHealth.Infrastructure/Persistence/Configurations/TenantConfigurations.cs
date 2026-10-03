using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Tenants;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", ApplicationDbContext.DefaultSchema);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
    }
}

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships", ApplicationDbContext.DefaultSchema);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.InviteeEmail).HasMaxLength(320).IsRequired();
        builder.Property(m => m.RevokeReason).HasMaxLength(1000);
        builder.Property(m => m.Disciplines).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.Ignore(m => m.IsActive);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);

        // One active membership per person per tenant, enforced by the database as well as by the handlers.
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique().HasFilter("status = 'Active'");
        builder.HasIndex(m => new { m.TenantId, m.InviteeEmail, m.Status });
        builder.HasIndex(m => new { m.UserId, m.Status });
    }
}
