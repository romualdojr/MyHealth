using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Tenants;

namespace MyHealth.Infrastructure.Persistence.Configurations;

internal sealed class NutritionAssessmentConfiguration : IEntityTypeConfiguration<NutritionAssessment>
{
    public void Configure(EntityTypeBuilder<NutritionAssessment> builder)
    {
        builder.ToTable("nutrition_assessments", ApplicationDbContext.DefaultSchema);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Content).HasJsonConversion();
        builder.Property(a => a.Corrections).HasJsonConversion();
        // The domain already has a Version (correction count), so the concurrency token is named after the system column.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.DietitianProfileId, a.ClientId });
        builder.HasIndex(a => new { a.ClientId, a.AssessedOn });
    }
}

internal sealed class MealPlanConfiguration : IEntityTypeConfiguration<MealPlan>
{
    public void Configure(EntityTypeBuilder<MealPlan> builder)
    {
        builder.ToTable("meal_plans", ApplicationDbContext.DefaultSchema);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.ComplexProperty(p => p.Details, MealPlanDetailsMapping.Configure);
        builder.Property(p => p.Draft).HasJsonConversion();
        builder.Property<uint>("version").IsRowVersion();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.DietitianProfileId, p.ClientId });
        builder.HasIndex(p => new { p.ClientId, p.Status });
    }
}

internal sealed class MealPlanVersionConfiguration : IEntityTypeConfiguration<MealPlanVersion>
{
    public void Configure(EntityTypeBuilder<MealPlanVersion> builder)
    {
        builder.ToTable("meal_plan_versions", ApplicationDbContext.DefaultSchema);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.ComplexProperty(v => v.Details, MealPlanDetailsMapping.Configure);
        builder.Property(v => v.Content).HasJsonConversion();
        builder.Property(v => v.ChangeDescription).HasMaxLength(1000).IsRequired();
        builder.HasIndex(v => new { v.MealPlanId, v.VersionNumber }).IsUnique();
        builder.HasIndex(v => v.ClientId);
        builder.HasOne<MealPlan>().WithMany().HasForeignKey(v => v.MealPlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class MealPlanDetailsMapping
{
    public static void Configure(ComplexPropertyBuilder<MealPlanDetails> details)
    {
        details.Property(d => d.Name).HasMaxLength(200).HasColumnName("name");
        details.Property(d => d.Goal).HasMaxLength(500).HasColumnName("goal");
        details.Property(d => d.StartDate).HasColumnName("start_date");
        details.Property(d => d.EndDate).HasColumnName("end_date");
        details.Property(d => d.Notes).HasMaxLength(4000).HasColumnName("notes");
    }
}

internal sealed class FoodDiaryEntryConfiguration : IEntityTypeConfiguration<FoodDiaryEntry>
{
    public void Configure(EntityTypeBuilder<FoodDiaryEntry> builder)
    {
        builder.ToTable("food_diary_entries", ApplicationDbContext.DefaultSchema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.MealName).HasMaxLength(100);
        builder.Property(e => e.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.ClientNotes).HasMaxLength(2000);
        builder.Property<uint>("version").IsRowVersion();

        builder.HasMany(e => e.Comments).WithOne().HasForeignKey("food_diary_entry_id").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Comments).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ClientId, e.LocalDate });
        builder.HasIndex(e => new { e.TenantId, e.DietitianProfileId, e.ClientId, e.LocalDate });
    }
}

internal sealed class FoodDiaryCommentConfiguration : IEntityTypeConfiguration<FoodDiaryComment>
{
    public void Configure(EntityTypeBuilder<FoodDiaryComment> builder)
    {
        builder.ToTable("food_diary_comments", ApplicationDbContext.DefaultSchema);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Message).HasMaxLength(2000).IsRequired();
    }
}
