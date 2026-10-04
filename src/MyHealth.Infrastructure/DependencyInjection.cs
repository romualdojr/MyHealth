using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Coaching;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Media;
using MyHealth.Application.Abstractions.MotionCapture;
using MyHealth.Application.Abstractions.Payments;
using MyHealth.Application.Abstractions.ProfessionalAssistant;
using MyHealth.Application.Abstractions.TrainerAssistant;
using MyHealth.Application.Abstractions.WhatsApp;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.Billing;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Application.Coaching;
using MyHealth.Application.MotionCapture;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Messaging;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;
using MyHealth.Domain.Wellbeing;
using MyHealth.Infrastructure.Auditing;
using MyHealth.Infrastructure.ClientExperience;
using MyHealth.Infrastructure.Coaching;
using MyHealth.Infrastructure.Identity;
using MyHealth.Infrastructure.Media;
using MyHealth.Infrastructure.Messaging;
using MyHealth.Infrastructure.Notifications;
using MyHealth.Infrastructure.Payments;
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
        services.AddScoped<IModulePriceRepository, ModulePriceRepository>();
        services.AddScoped<ITenantSubscriptionRepository, TenantSubscriptionRepository>();
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<IPaymentEventRepository, PaymentEventRepository>();

        services.AddScoped<IWellbeingCheckInRepository, WellbeingCheckInRepository>();
        services.AddScoped<IAssistantConsentRepository, AssistantConsentRepository>();
        services.AddScoped<IAssistantProposalRepository, AssistantProposalRepository>();
        services.AddSingleton<IMediaHostPolicy, ConfiguredMediaHostPolicy>();

        // Assistant: the rule-based fake unless Claude is configured (Assistant:Provider=Anthropic plus an API key).
        if (string.Equals(builder.Configuration["Assistant:Provider"], "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = builder.Configuration["Assistant:Anthropic:ApiKey"]
                         ?? throw new InvalidOperationException("Assistant:Provider is Anthropic but Assistant:Anthropic:ApiKey is not set.");
            services.AddHttpClient<AnthropicAssistantModel>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
            services.AddScoped<IAssistantModel>(sp => sp.GetRequiredService<AnthropicAssistantModel>());
        }
        else
        {
            services.AddSingleton<IAssistantModel, FakeAssistantModel>();
        }

        // Trainer's AI coach: rule-based unless Claude is configured (Coach:Provider=Anthropic). Analyses are capped per tenant per day.
        services.AddScoped<ICoachAnalysisRepository, CoachAnalysisRepository>();
        services.AddSingleton(new CoachSettings(
            builder.Configuration.GetValue("Coach:DailyAnalysesPerTenant", 100), builder.Configuration.GetValue("Coach:DailyChatMessagesPerTenant", 300)));
        if (string.Equals(builder.Configuration["Coach:Provider"], "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = builder.Configuration["Coach:Anthropic:ApiKey"] ?? builder.Configuration["Assistant:Anthropic:ApiKey"]
                         ?? throw new InvalidOperationException("Coach:Provider is Anthropic but neither Coach:Anthropic:ApiKey nor Assistant:Anthropic:ApiKey is set.");
            services.AddHttpClient<AnthropicTrainingCoachModel>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                client.Timeout = TimeSpan.FromSeconds(60);
            });
            services.AddScoped<ITrainingCoachModel>(sp => sp.GetRequiredService<AnthropicTrainingCoachModel>());
        }
        else
        {
            services.AddSingleton<ITrainingCoachModel, RuleBasedTrainingCoachModel>();
        }

        // Trainer's chat: rule-based unless Claude is configured (TrainerAssistant:Provider=Anthropic).
        if (string.Equals(builder.Configuration["TrainerAssistant:Provider"], "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = builder.Configuration["TrainerAssistant:Anthropic:ApiKey"] ?? builder.Configuration["Coach:Anthropic:ApiKey"]
                         ?? builder.Configuration["Assistant:Anthropic:ApiKey"]
                         ?? throw new InvalidOperationException("TrainerAssistant:Provider is Anthropic but no Anthropic API key is set.");
            services.AddHttpClient<AnthropicTrainerAssistantModel>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                client.Timeout = TimeSpan.FromSeconds(60);
            });
            services.AddScoped<ITrainerAssistantModel>(sp => sp.GetRequiredService<AnthropicTrainerAssistantModel>());
        }
        else
        {
            services.AddSingleton<ITrainerAssistantModel, RuleBasedTrainerAssistantModel>();
        }

        // Dietitians' and therapy practitioners' chat: follows the trainers' chat unless ProfessionalAssistant:Provider is set.
        var professionalProvider = builder.Configuration["ProfessionalAssistant:Provider"] ?? builder.Configuration["TrainerAssistant:Provider"];
        if (string.Equals(professionalProvider, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = builder.Configuration["ProfessionalAssistant:Anthropic:ApiKey"] ?? builder.Configuration["TrainerAssistant:Anthropic:ApiKey"]
                         ?? builder.Configuration["Coach:Anthropic:ApiKey"] ?? builder.Configuration["Assistant:Anthropic:ApiKey"]
                         ?? throw new InvalidOperationException("ProfessionalAssistant:Provider is Anthropic but no Anthropic API key is set.");
            services.AddHttpClient<AnthropicProfessionalAssistantModel>(client =>
            {
                client.BaseAddress = new Uri("https://api.anthropic.com/");
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                // A full meal plan is a long answer.
                client.Timeout = TimeSpan.FromSeconds(120);
            });
            services.AddScoped<IProfessionalAssistantModel>(sp => sp.GetRequiredService<AnthropicProfessionalAssistantModel>());
        }
        else
        {
            services.AddSingleton<IProfessionalAssistantModel, RuleBasedProfessionalAssistantModel>();
        }

        // Media storage (RF-27): local disk for development; production swaps in a bucket behind the CDN.
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();

        // 3D exercise videos: DeepMotion when configured (MotionCapture:Provider=DeepMotion), a fake otherwise.
        services.AddScoped<IMotionCaptureJobRepository, MotionCaptureJobRepository>();
        services.AddSingleton(new MotionCaptureSettings(
            builder.Configuration.GetValue("MotionCapture:MaxUploadMegabytes", 200) * 1024L * 1024L,
            builder.Configuration.GetValue("MotionCapture:MonthlyJobsPerTenant", 30),
            builder.Configuration.GetValue("MotionCapture:DeleteSourceAfterProcessing", true),
            TimeSpan.FromSeconds(builder.Configuration.GetValue("MotionCapture:PollIntervalSeconds", 20))));
        if (string.Equals(builder.Configuration["MotionCapture:Provider"], "DeepMotion", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = builder.Configuration["MotionCapture:DeepMotion:ApiBaseUrl"]
                          ?? throw new InvalidOperationException("MotionCapture:Provider is DeepMotion but MotionCapture:DeepMotion:ApiBaseUrl is not set.");
            if (string.IsNullOrEmpty(builder.Configuration["MotionCapture:DeepMotion:ClientId"]) || string.IsNullOrEmpty(builder.Configuration["MotionCapture:DeepMotion:ClientSecret"]))
            {
                throw new InvalidOperationException("MotionCapture:DeepMotion:ClientId and ClientSecret must be set (user secrets or a vault).");
            }

            // One client for the process lifetime: its cookie container holds the DeepMotion session.
            services.AddSingleton<IMotionCaptureProvider>(sp => new DeepMotionMotionCaptureProvider(
                new HttpClient(new SocketsHttpHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer(), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(60),
                },
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeepMotionMotionCaptureProvider>>()));
        }
        else
        {
            services.AddSingleton<IMotionCaptureProvider, FakeMotionCaptureProvider>();
        }

        if (builder.Configuration.GetValue("MotionCapture:WorkerEnabled", true))
        {
            services.AddHostedService<MotionCaptureWorkerService>();
        }

        // WhatsApp (§8, RF-08): a fake provider until one is chosen. A real one replaces this registration only.
        services.AddScoped<IWhatsAppContactRepository, WhatsAppContactRepository>();
        services.AddSingleton<FakeWhatsAppProvider>();
        services.AddSingleton<IWhatsAppProvider>(sp => sp.GetRequiredService<FakeWhatsAppProvider>());

        // Payments (§8): a fake provider until one is chosen. A real one replaces both registrations; it simulates nothing.
        services.AddSingleton<FakePaymentProvider>();
        services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());
        services.AddSingleton<IPaymentSimulator>(sp => sp.GetRequiredService<FakePaymentProvider>());
        if (builder.Configuration.GetValue("Billing:CycleEnabled", true))
        {
            services.AddHostedService<BillingCycleService>();
        }

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
