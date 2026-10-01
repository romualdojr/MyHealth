using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;
using MyHealth.Infrastructure.Auditing;
using MyHealth.Infrastructure.Identity;
using MyHealth.Infrastructure.Notifications;
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
        services.AddScoped<IHealthHistoryRepository, HealthHistoryRepository>();
        services.AddScoped<IPhysicalAssessmentRepository, PhysicalAssessmentRepository>();
        services.AddScoped<INutritionAssessmentRepository, NutritionAssessmentRepository>();
        services.AddScoped<IMealPlanRepository, MealPlanRepository>();
        services.AddScoped<IMealPlanVersionRepository, MealPlanVersionRepository>();
        services.AddScoped<IFoodDiaryRepository, FoodDiaryRepository>();
        services.AddScoped<IPrivacyRequestRepository, PrivacyRequestRepository>();
        services.AddScoped<IRetentionPolicyRepository, RetentionPolicyRepository>();
        services.AddScoped<IPushDeviceRepository, PushDeviceRepository>();
        services.AddScoped<ITherapyModalityRepository, TherapyModalityRepository>();
        services.AddScoped<IPractitionerQualificationRepository, PractitionerQualificationRepository>();
        services.AddScoped<ITherapyServiceRepository, TherapyServiceRepository>();
        services.AddScoped<IConsentTemplateRepository, ConsentTemplateRepository>();
        services.AddScoped<ITherapyConsentRepository, TherapyConsentRepository>();
        services.AddScoped<ITherapySessionRepository, TherapySessionRepository>();
        services.AddScoped<ITherapyCarePlanRepository, TherapyCarePlanRepository>();

        // RNF-02: professionals and administrators need two-factor authentication. Turn off only for local development.
        services.AddSingleton(new SecurityPolicy(builder.Configuration.GetValue("Security:RequireTwoFactorForProfessionals", true)));
        services.AddScoped<IAdministratorDirectory, AdministratorDirectory>();

        // Notification outbox. The logging sender stands in until push and e-mail providers are chosen (§8).
        services.AddSingleton<LoggingNotificationSender>();
        services.AddSingleton<IPushSender>(sp => sp.GetRequiredService<LoggingNotificationSender>());
        services.AddSingleton<INotificationEmailSender>(sp => sp.GetRequiredService<LoggingNotificationSender>());
        services.AddScoped<NotificationDispatcher>();
        if (builder.Configuration.GetValue("Notifications:DispatcherEnabled", true))
        {
            services.AddHostedService<NotificationDispatcherService>();
        }

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
