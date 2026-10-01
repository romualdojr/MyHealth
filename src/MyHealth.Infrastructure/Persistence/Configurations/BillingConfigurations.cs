using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Tenants;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class ModulePriceConfiguration : IEntityTypeConfiguration<ModulePrice>
{
    public void Configure(EntityTypeBuilder<ModulePrice> builder)
    {
        builder.ToTable("billing_module_prices", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasIndex(p => new { p.Module, p.EffectiveFrom });
    }
}

internal sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.ToTable("billing_subscriptions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.History).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithOne().HasForeignKey<TenantSubscription>(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.TenantId).IsUnique();
        builder.HasIndex(s => new { s.Status, s.NextInvoiceAt });
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("billing_invoices", ApplicationDbContext.DefaultSchema);
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Lines).HasJsonConversion();
        builder.Property(i => i.Currency).HasMaxLength(3).IsRequired();
        builder.Property(i => i.PaymentReference).HasMaxLength(1100);
        builder.Property(i => i.VoidReason).HasMaxLength(1000);
        builder.OwnsOne(i => i.Charge, charge =>
        {
            charge.Property(c => c.Provider).HasMaxLength(32).HasColumnName("charge_provider");
            charge.Property(c => c.ChargeId).HasMaxLength(128).HasColumnName("charge_id");
            charge.Property(c => c.PixCopyPaste).HasMaxLength(1024).HasColumnName("charge_pix_copy_paste");
            charge.Property(c => c.CheckoutUrl).HasMaxLength(2048).HasColumnName("charge_checkout_url");
            charge.Property(c => c.ExpiresAt).HasColumnName("charge_expires_at");
            charge.HasIndex(c => new { c.Provider, c.ChargeId });
        });
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TenantSubscription>().WithMany().HasForeignKey(i => i.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => new { i.SubscriptionId, i.Status });
        builder.HasIndex(i => new { i.TenantId, i.PeriodStart });
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("billing_payment_events", ApplicationDbContext.DefaultSchema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Provider).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ProviderEventId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Type).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(32).IsRequired();

        // The guarantee behind webhook idempotency: two deliveries of one event cannot both be recorded (409 → retry → duplicate).
        builder.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique();
    }
}
