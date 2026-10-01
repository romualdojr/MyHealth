using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;
using MyHealth.Infrastructure.Auditing;
using MyHealth.Infrastructure.Identity;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Aspire resource name of the PostgreSQL database (see AppHost).</summary>
    public const string DatabaseResourceName = "myhealthdb";

    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        // Aspire integration: connection string from service discovery, health check, retries, tracing and metrics.
        builder.AddNpgsqlDbContext<ApplicationDbContext>(
            DatabaseResourceName,
            configureDbContextOptions: options => ConfigureDbContext(options));

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IReadDbContext, ReadDbContext>();
        services.AddScoped<IAuditLog, AuditLog>();

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IUserProfileRepository, UserProfileRepository>();
        services.AddScoped<IProfessionalProfileRepository, ProfessionalProfileRepository>();
        services.AddScoped<ICareRelationshipRepository, CareRelationshipRepository>();
        services.AddScoped<IExerciseRepository, ExerciseRepository>();
        services.AddScoped<ITrainingPlanRepository, TrainingPlanRepository>();
        services.AddScoped<ITrainingPlanVersionRepository, TrainingPlanVersionRepository>();
        services.AddScoped<IWorkoutSessionRepository, WorkoutSessionRepository>();
        services.AddScoped<IWorkoutHeartRateRepository, WorkoutHeartRateRepository>();
        services.AddScoped<IHealthConnectionRepository, HealthConnectionRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();

        services
            .AddIdentityApiEndpoints<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.SignIn.RequireConfirmedEmail = builder.Configuration.GetValue("Identity:RequireConfirmedEmail", true);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        return builder;
    }

    internal static DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder options) =>
        options
            .UseSnakeCaseNamingConvention()
            .UseNpgsql(npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ApplicationDbContext.DefaultSchema));
}
