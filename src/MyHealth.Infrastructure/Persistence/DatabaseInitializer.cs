using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Infrastructure.Identity;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds reference data: roles, the curated exercise library, an optional bootstrap admin and, in
/// Development, sample accounts and records (<see cref="DevelopmentSeeder"/>).
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ApplicationDbContext>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        await db.Database.MigrateAsync(cancellationToken);

        var roles = provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync(Roles.Administrator))
        {
            await roles.CreateAsync(new IdentityRole<Guid>(Roles.Administrator) { Id = Guid.CreateVersion7() });
        }

        await SeedExercisesAsync(db, provider.GetRequiredService<TimeProvider>(), cancellationToken);
        await SeedTherapyModalitiesAsync(db, provider.GetRequiredService<TimeProvider>(), cancellationToken);
        await SeedModulePricesAsync(db, provider.GetRequiredService<TimeProvider>(), cancellationToken);
        await SeedAdministratorAsync(provider, logger);

        // Sample accounts and records for local work: Development only, and only when Seed:Development:Enabled is true.
        var configuration = provider.GetRequiredService<IConfiguration>();
        if (provider.GetService<IHostEnvironment>()?.IsDevelopment() == true && configuration.GetValue("Seed:Development:Enabled", false))
        {
            await new DevelopmentSeeder(services, configuration, provider.GetRequiredService<TimeProvider>(), logger).SeedAsync(cancellationToken);
        }
    }

    private static async Task SeedExercisesAsync(ApplicationDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await db.Exercises.AnyAsync(e => e.TenantId == null, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        ExerciseDetails[] library =
        [
            new("Agachamento livre", "Barra apoiada no trapézio; desça controlando até a coxa ficar paralela ao chão.", ["quadriceps", "gluteos"], ["barra"], "força", "Mantenha a coluna neutra.", null),
            new("Supino reto", "Deitado no banco, desça a barra até o peito e empurre até estender os cotovelos.", ["peitoral", "triceps"], ["barra", "banco"], "força", "Use apoio de segurança ou um parceiro.", null),
            new("Remada curvada", "Tronco inclinado, puxe a barra em direção ao abdômen.", ["dorsais", "biceps"], ["barra"], "força", "Evite arredondar a lombar.", null),
            new("Levantamento terra", "Puxe a barra do chão estendendo quadril e joelhos ao mesmo tempo.", ["posterior de coxa", "gluteos", "lombar"], ["barra"], "força", "Requer técnica supervisionada.", null),
            new("Desenvolvimento com halteres", "Sentado, empurre os halteres acima da cabeça.", ["ombros", "triceps"], ["halteres", "banco"], "força", null, null),
            new("Puxada frontal", "Puxe a barra da polia até a altura do queixo.", ["dorsais", "biceps"], ["polia"], "força", null, null),
            new("Leg press 45°", "Empurre a plataforma estendendo os joelhos sem travá-los.", ["quadriceps", "gluteos"], ["maquina"], "força", "Não tire o quadril do banco.", null),
            new("Prancha", "Apoio nos antebraços e pés, corpo alinhado.", ["core"], [], "estabilidade", null, null),
            new("Esteira", "Caminhada ou corrida em esteira.", ["cardiorrespiratorio"], ["esteira"], "aeróbico", null, null),
            new("Bicicleta ergométrica", "Pedalada em bicicleta estacionária.", ["cardiorrespiratorio"], ["bicicleta"], "aeróbico", null, null),
        ];

        db.Exercises.AddRange(library.Select(details => Exercise.CreateSystem(details, now)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Acupuncture is seeded disabled and with no accepted councils: which professions may practise it under Law 15,345/2026
    /// is a legal decision (§9). Operators configure it and enable it with the reference of that review.
    /// </summary>
    private static async Task SeedTherapyModalitiesAsync(ApplicationDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await db.TherapyModalities.AnyAsync(m => m.Code == TherapyModality.Acupuncture, cancellationToken))
        {
            return;
        }

        var acupuncture = TherapyModality.Define(
            TherapyModality.Acupuncture, "Acupuntura", "Aguardando revisão jurídica e clínica antes da habilitação.", [],
            TherapyModality.AcupunctureDefaultRequiredFields, requiresConsent: true, clock.GetUtcNow());
        db.TherapyModalities.Add(acupuncture.Value);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Placeholder monthly prices so billing works out of the box (R$ 99 training, R$ 49 nutrition, R$ 49 therapies).
    /// The real prices are a commercial decision (§12.2): operators set them in the back office.
    /// </summary>
    private static async Task SeedModulePricesAsync(ApplicationDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await db.ModulePrices.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        (BillingModule Module, long Cents)[] placeholders = [(BillingModule.Training, 9_900), (BillingModule.Nutrition, 4_900), (BillingModule.Therapies, 4_900)];
        db.ModulePrices.AddRange(placeholders.Select(p => ModulePrice.Set(p.Module, p.Cents, now, null, now).Value));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedAdministratorAsync(IServiceProvider provider, ILogger logger)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var email = configuration["Seed:AdminEmail"];
        var password = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded)
            {
                logger.LogWarning("Bootstrap administrator could not be created: {Errors}", string.Join(", ", created.Errors.Select(e => e.Code)));
                return;
            }
        }

        if (!await users.IsInRoleAsync(admin, Roles.Administrator))
        {
            await users.AddToRoleAsync(admin, Roles.Administrator);
        }
    }
}
