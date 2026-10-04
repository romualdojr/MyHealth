using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Assessments;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.Exercises;
using MyHealth.Application.HealthData;
using MyHealth.Application.Nutrition;
using MyHealth.Application.Professionals;
using MyHealth.Application.Tenants;
using MyHealth.Application.Therapies;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Users;
using MyHealth.Application.Wellbeing;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Plans;
using MyHealth.Infrastructure.Identity;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>
/// Development data so every screen has something to show: an operator, an organization with a verified trainer and a
/// dietitian, three clients (two linked, one invitation pending), published training plans and a few weeks of
/// workouts, Ana's nutrition follow-up (assessment, meal plan and diary) and more of the dietitian's patients at
/// different stages (<see cref="SeedNutritionPatientsAsync"/>). Accounts and password are in
/// <c>Seed:Development</c> (appsettings.Development.json).
/// <para>
/// Each step runs the same application command the API runs, as the person who would do it (a signed-in user and the
/// selected organization are set on the request context), so domain rules, trials, grants, versions and notifications
/// behave exactly as in real use. Runs once: skipped when the operator account already exists (the nutrition patients
/// have their own check, so they reach databases seeded before them). Never enabled outside
/// Development and Staging (the demo environment, with its own password).
/// </para>
/// </summary>
public sealed class DevelopmentSeeder(IServiceProvider services, IConfiguration configuration, TimeProvider clock, ILogger logger)
{
    private const string TimeZone = "America/Sao_Paulo";

    private sealed record Account(Guid Id, string Email, string Name, bool IsOperator = false);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var section = configuration.GetSection("Seed:Development");
        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:Development:Password is not set; development data was not seeded.");
            return;
        }

        string Email(string key, string fallback) => section[$"{key}Email"] ?? fallback;

        if (await FindAccountAsync(Email("Operator", "operador@myhealth.dev")) is null)
        {
            await SeedOrganizationAsync(password, Email, cancellationToken);
        }
        else
        {
            logger.LogInformation("Development data already seeded.");
        }

        // Added later than the rest, so they run on databases seeded before they existed too.
        await SeedNutritionPatientsAsync(password, Email, cancellationToken);
        await SeedAssistantAsync(password, Email, cancellationToken);
    }

    private async Task<ApplicationUser?> FindAccountAsync(string email)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    private async Task SeedOrganizationAsync(string password, Func<string, string, string> Email, CancellationToken cancellationToken)
    {
        var @operator = await AccountAsync(Email("Operator", "operador@myhealth.dev"), "Olga Operadora", password, isOperator: true);
        var trainer = await AccountAsync(Email("Trainer", "treinador@myhealth.dev"), "Tina Treinadora", password);
        var dietitian = await AccountAsync(Email("Dietitian", "nutri@myhealth.dev"), "Nina Nutricionista", password);
        var ana = await AccountAsync(Email("ClientAna", "ana@myhealth.dev"), "Ana Souza", password);
        var bruno = await AccountAsync(Email("ClientBruno", "bruno@myhealth.dev"), "Bruno Lima", password);
        var carla = await AccountAsync(Email("ClientCarla", "carla@myhealth.dev"), "Carla Mendes", password);

        foreach (var person in new[] { trainer, dietitian, ana, bruno, carla })
        {
            await RunAsync(person, null, new CompleteProfileCommand(person.Name, TimeZone));
        }

        // Professionals: registered by themselves, verified by the operator.
        var trainerProfile = await RunAsync(trainer, null, new CreateProfessionalProfileCommand(
            Discipline.PersonalTrainer, "Tina Treinadora", "Treino de força e condicionamento para iniciantes.", null, new RegistrationInput("CREF", "012345-G", "SP")));
        var dietitianProfile = await RunAsync(dietitian, null, new CreateProfessionalProfileCommand(
            Discipline.Dietitian, "Nina Nutricionista", "Reeducação alimentar.", null, new RegistrationInput("CRN", "54321", "3")));
        await RunAsync(@operator, null, new VerifyProfessionalCommand(trainerProfile, "Seed de desenvolvimento"));
        await RunAsync(@operator, null, new VerifyProfessionalCommand(dietitianProfile, "Seed de desenvolvimento"));

        // The organization (trial with every module), the trainer as administrator, the dietitian as member.
        var tenant = await RunAsync(trainer, null, new CreateTenantCommand("Estúdio Movimento", TenantKind.Organization, [Discipline.PersonalTrainer, Discipline.Dietitian]));
        var membership = await RunAsync(trainer, tenant, new InviteMemberCommand(dietitian.Email, [Discipline.Dietitian], IsAdministrator: false));
        await RunAsync(dietitian, null, new RespondToMembershipInvitationCommand(membership, InvitationResponse.Accept));

        // Clients: Ana and Bruno accepted and share their training; Carla's invitation is pending.
        var anaLink = await RunAsync(trainer, tenant, new InviteClientCommand(Discipline.PersonalTrainer, ana.Email));
        await RunAsync(ana, null, new RespondToInvitationCommand(anaLink, InvitationResponse.Accept));
        foreach (var category in new[] { DataCategory.WorkoutSummary, DataCategory.Wellbeing, DataCategory.AiCoaching, DataCategory.HeartRate })
        {
            await RunAsync(ana, null, new ChangeDataSharingCommand(anaLink, category, Shared: true));
        }

        var brunoLink = await RunAsync(trainer, tenant, new InviteClientCommand(Discipline.PersonalTrainer, bruno.Email));
        await RunAsync(bruno, null, new RespondToInvitationCommand(brunoLink, InvitationResponse.Accept));
        await RunAsync(bruno, null, new ChangeDataSharingCommand(brunoLink, DataCategory.WorkoutSummary, Shared: true));
        await RunAsync(trainer, tenant, new InviteClientCommand(Discipline.PersonalTrainer, carla.Email));

        // The organization's own exercise, next to the curated library.
        var benchSquat = await RunAsync(trainer, tenant, new CreateExerciseCommand(new ExerciseInput(
            "Agachamento no banco", "Sente e levante do banco sem apoiar as mãos, pés firmes no chão.", ["quadríceps", "glúteos"], ["banco"], "força",
            "Não deixe os joelhos caírem para dentro.", "Pessoa senta em um banco e levanta sem usar as mãos, de frente e de lado.")));

        var library = await LibraryAsync(cancellationToken);
        Guid Exercise(string name) => library.TryGetValue(name, out var id) ? id : throw new InvalidOperationException($"Library exercise '{name}' is missing.");

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Ana: two strength workouts and a walk; she is at the top of the range in the squat, so the AI coach proposes a progression.
        var anaPlan = await PlanAsync(trainer, tenant, ana, new PlanDetailsInput(
                "Força — fase 1", "Ganhar força e confiança nos treinos", TrainingLevel.Beginner, today.AddDays(-21), null, 3, "Beba água e respeite o descanso."),
            [
                Strength("Treino A", Reps(Exercise("Agachamento livre"), 3, 8, 12, 40m, 90), Reps(Exercise("Supino reto"), 3, 8, 12, 30m, 90), Timed(Exercise("Prancha"), 3, 30, 45)),
                Strength("Treino B", Reps(Exercise("Levantamento terra"), 3, 8, 10, 50m, 120), Reps(Exercise("Puxada frontal"), 3, 10, 12, 35m, 90), Reps(benchSquat, 3, 12, 15, null, 60)),
                new PlannedSessionInput(null, "Caminhada", SessionKind.Aerobic, null, [], new AerobicTarget("Caminhada na esteira", 30, null, "Moderada, consegue conversar", "5 min leve", "5 min leve")),
            ]);
        await WorkoutAsync(ana, anaPlan, "Treino A", daysAgo: 12, exertion: 7, atTop: false, cancellationToken: cancellationToken);
        await WorkoutAsync(ana, anaPlan, "Treino B", daysAgo: 10, exertion: 7, atTop: false, cancellationToken: cancellationToken);
        var anaRecent = await WorkoutAsync(ana, anaPlan, "Treino A", daysAgo: 7, exertion: 6, atTop: true, comments: "Senti que dava para mais carga.", cancellationToken: cancellationToken);
        await WorkoutAsync(ana, anaPlan, "Treino B", daysAgo: 5, exertion: 7, atTop: false, cancellationToken: cancellationToken);
        var anaLatest = await WorkoutAsync(ana, anaPlan, "Treino A", daysAgo: 2, exertion: 6, atTop: true, comments: "Fácil hoje!", cancellationToken: cancellationToken);

        // Her Apple Watch heart rate for the last two workouts, shared with the trainer.
        await RunAsync(ana, null, new ChangeHealthConnectionCommand(HealthConnectionAction.Connect));
        await HeartRateAsync(ana, anaRecent, cancellationToken);
        await HeartRateAsync(ana, anaLatest, cancellationToken);
        await RunAsync(trainer, tenant, new ReplyToFeedbackCommand(anaRecent, "Ótimo! Na próxima semana vamos aumentar a carga do agachamento."));
        for (var day = 1; day <= 5; day++)
        {
            await RunAsync(ana, null, new SaveWellbeingCheckInCommand(today.AddDays(-day), 4, day == 3 ? 3 : 4, null));
        }

        // Bruno: one workout with a discomfort report still waiting for the trainer's reply.
        var brunoPlan = await PlanAsync(trainer, tenant, bruno, new PlanDetailsInput(
                "Condicionamento geral", "Voltar a treinar com regularidade", TrainingLevel.Beginner, today.AddDays(-10), null, 2, null),
            [Strength("Treino único", Reps(Exercise("Supino reto"), 3, 10, 12, 20m, 60), Reps(Exercise("Remada curvada"), 3, 10, 12, 20m, 60), Timed(Exercise("Prancha"), 2, 20, 30))]);
        await WorkoutAsync(bruno, brunoPlan, "Treino único", daysAgo: 1, exertion: 8, atTop: false,
            discomfort: "Leve dor no ombro direito no supino.", comments: "Cansei bastante.", cancellationToken: cancellationToken);

        // Nutrition: Ana also sees the dietitian, who assessed her, published a meal plan and follows her diary.
        await NutritionAsync(dietitian, tenant, ana, today, cancellationToken);

        // Bruno started with the dietitian too: no assessment or plan yet (Ana's plan can be copied for him).
        var brunoNutrition = await RunAsync(dietitian, tenant, new InviteClientCommand(Discipline.Dietitian, bruno.Email));
        await RunAsync(bruno, null, new RespondToInvitationCommand(brunoNutrition, InvitationResponse.Accept));

        logger.LogInformation(
            "Development data seeded: operator {Operator}, trainer {Trainer} (organization Estúdio Movimento), clients {Ana}, {Bruno}; password in Seed:Development:Password.",
            @operator.Email, trainer.Email, ana.Email, bruno.Email);
    }

    private async Task<Account> AccountAsync(string email, string name, string password, bool isOperator = false)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"Seed account {email} could not be created: {string.Join(", ", created.Errors.Select(e => e.Code))}");
        }

        if (isOperator)
        {
            await users.AddToRoleAsync(user, Roles.Administrator);
        }

        return new Account(user.Id, email, name, isOperator);
    }

    /// <summary>Runs a command as <paramref name="who"/>, acting for <paramref name="tenantId"/> when given, as an API request would.</summary>
    private Task<TResult> RunAsync<TResult>(Account who, Guid? tenantId, ICommand<TResult> command) =>
        AsAsync(who, tenantId, async provider =>
        {
            var result = await InvokeAsync<Result<TResult>>(provider, typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult)), command);
            return result.IsSuccess ? result.Value : throw Failed(command, result.Error);
        });

    private async Task RunAsync(Account who, Guid? tenantId, ICommand command) =>
        await AsAsync<object?>(who, tenantId, async provider =>
        {
            var result = await InvokeAsync<Result>(provider, typeof(ICommandHandler<>).MakeGenericType(command.GetType()), command);
            return result.IsSuccess ? null : throw Failed(command, result.Error);
        });

    /// <summary>Calls the registered handler (with its validation and logging decorators) through the public interface.</summary>
    private static Task<T> InvokeAsync<T>(IServiceProvider provider, Type handlerType, object command) =>
        (Task<T>)handlerType.GetMethod("Handle")!.Invoke(provider.GetRequiredService(handlerType), [command, CancellationToken.None])!;

    private async Task<T> AsAsync<T>(Account who, Guid? tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, who.Id.ToString()),
            new(ClaimTypes.Name, who.Email),
            new(ClaimTypes.Email, who.Email),
            new(UserContext.AuthenticationMethodClaim, UserContext.MultiFactorMethod),
        };
        if (who.IsOperator)
        {
            claims.Add(new Claim(ClaimTypes.Role, Roles.Administrator));
        }

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "development-seed")) };
        if (tenantId is { } id)
        {
            http.Request.Headers[TenantHeader.Name] = id.ToString();
        }

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = http;
        try
        {
            return await action(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static InvalidOperationException Failed(object command, Error error) =>
        new($"Development seed step {command.GetType().Name} failed: {error.Code} — {error.Description}");

    private async Task<Dictionary<string, Guid>> LibraryAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Exercises.Where(e => e.TenantId == null).ToDictionaryAsync(e => e.Name, e => e.Id, cancellationToken);
    }

    private sealed record SeededPlan(Guid Id, PlanContent Content);

    private async Task<SeededPlan> PlanAsync(Account trainer, Guid tenant, Account client, PlanDetailsInput details, IReadOnlyList<PlannedSessionInput> sessions)
    {
        var planId = await RunAsync(trainer, tenant, new CreateTrainingPlanCommand(client.Id, details));
        await RunAsync(trainer, tenant, new UpdateTrainingPlanSessionsCommand(planId, sessions));
        await RunAsync(trainer, tenant, new PublishTrainingPlanCommand(planId, "Primeira versão do plano"));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.TrainingPlans.AsNoTracking().SingleAsync(p => p.Id == planId);
        return new SeededPlan(planId, plan.Draft);
    }

    /// <summary>A completed workout with every prescribed set logged: at the top of the range, or one or two repetitions below it.</summary>
    private async Task<Guid> WorkoutAsync(
        Account client, SeededPlan plan, string sessionName, int daysAgo, int exertion, bool atTop, string? discomfort = null, string? comments = null,
        CancellationToken cancellationToken = default)
    {
        var session = plan.Content.Sessions.Single(s => s.Name == sessionName);
        var startedAt = clock.GetUtcNow().AddDays(-daysAgo).Date.AddHours(21).AddMinutes(-daysAgo % 3 * 20);
        var startedAtOffset = new DateTimeOffset(startedAt, TimeSpan.Zero);
        var workoutId = Guid.CreateVersion7(startedAtOffset);
        await RunAsync(client, null, new StartWorkoutCommand(workoutId, plan.Id, session.Id, startedAtOffset, TimeZone));

        var minute = 3;
        foreach (var exercise in session.Exercises.OrderBy(e => e.Order))
        {
            foreach (var target in exercise.Sets.OrderBy(t => t.Order))
            {
                var repetitions = target.MaxRepetitions is { } max ? (atTop ? max : Math.Max(target.MinRepetitions ?? max, max - 2)) : (int?)null;
                await RunAsync(client, null, new LogSetCommand(
                    workoutId, Guid.CreateVersion7(), exercise.ExerciseId, exercise.Id, target.Order, repetitions, target.SuggestedLoadKg, target.DurationSeconds,
                    null, exertion, null, startedAtOffset.AddMinutes(minute)));
                minute += 3;
            }
        }

        await RunAsync(client, null, new CompleteWorkoutCommand(
            workoutId, startedAtOffset.AddMinutes(minute + 5), exertion, discomfort is not null, discomfort, comments));
        return workoutId;
    }

    /// <summary>
    /// An assessment with a follow-up in two weeks, a published meal plan and four days of diary: most meals as planned, one
    /// unplanned, one entry already commented and the rest waiting for the dietitian.
    /// </summary>
    private async Task NutritionAsync(Account dietitian, Guid tenant, Account client, DateOnly today, CancellationToken cancellationToken)
    {
        var link = await RunAsync(dietitian, tenant, new InviteClientCommand(Discipline.Dietitian, client.Email));
        await RunAsync(client, null, new RespondToInvitationCommand(link, InvitationResponse.Accept));

        await RunAsync(dietitian, tenant, new RecordNutritionAssessmentCommand(client.Id, new NutritionAssessmentInput(
            today.AddDays(-14), "Mais energia nos treinos e menos doces à noite", null, "Amendoim", "Gosta de frutas e de comida caseira; não gosta de peixe",
            "Acorda 6h30, almoça 12h30, treina 20h, janta 21h30",
            [new MeasurementInput("body_mass", "scale", "kg", 68.5m), new MeasurementInput("waist_circumference", "tape", "cm", 78m)],
            "Diário de 4 dias por semana; retorno com o diário preenchido.", today.AddDays(14), null)));

        var plan = await MealPlanAsync(dietitian, tenant, client, new MealPlanDetailsInput(
            "Reeducação alimentar", "Mais energia nos treinos", today.AddDays(-13), null, "Beba 2 litros de água por dia."),
        [
            new PlannedMealInput(null, "Café da manhã", new TimeOnly(7, 0), null,
            [
                new MealItemInput(null, "Pão integral", null, null, "2 fatias", null, ["Tapioca (2 colheres de sopa)", "Cuscuz (1 xícara)"]),
                new MealItemInput(null, "Ovo mexido", 2m, "unidade", null, null, null),
                new MealItemInput(null, "Café com leite", 200m, "ml", null, "Sem açúcar", null),
            ]),
            new PlannedMealInput(null, "Almoço", new TimeOnly(12, 30), null,
            [
                new MealItemInput(null, "Arroz integral", 100m, "g", "4 colheres de sopa", null, null),
                new MealItemInput(null, "Feijão", null, null, "1 concha", null, null),
                new MealItemInput(null, "Frango grelhado", 120m, "g", null, null, ["Carne magra (120 g)"]),
                new MealItemInput(null, "Salada de folhas", null, null, "À vontade", null, null),
            ]),
            new PlannedMealInput(null, "Lanche pré-treino", new TimeOnly(18, 30), null,
            [new MealItemInput(null, "Banana", 1m, "unidade", null, null, ["Maçã (1 unidade)"]), new MealItemInput(null, "Iogurte natural", 170m, "g", null, null, null)]),
            new PlannedMealInput(null, "Jantar", new TimeOnly(21, 30), "Refeição leve depois do treino.",
            [new MealItemInput(null, "Omelete de legumes", null, null, "2 ovos", null, null), new MealItemInput(null, "Salada de folhas", null, null, "À vontade", null, null)]),
        ], cancellationToken: cancellationToken);

        var commented = await MealAsync(client, plan, today, 4, "Café da manhã", 7, MealAdherence.AsPlanned);
        await MealAsync(client, plan, today, 4, "Almoço", 13, MealAdherence.Partly, "Comi arroz branco no restaurante do trabalho.");
        await MealAsync(client, plan, today, 3, "Café da manhã", 7, MealAdherence.AsPlanned);
        await MealAsync(client, plan, today, 3, "Lanche pré-treino", 18, MealAdherence.AsPlanned);
        await MealAsync(client, plan, today, 2, "Jantar", 21, MealAdherence.Unplanned, "Pizza com amigos.", "Fiquei com muita fome depois do treino.");
        await MealAsync(client, plan, today, 1, "Café da manhã", 7, MealAdherence.AsPlanned);
        await MealAsync(client, plan, today, 1, "Almoço", 12, MealAdherence.AsPlanned);
        await WaterAsync(client, link, today, 1, 2000);
        await RunAsync(dietitian, tenant, new CommentOnFoodDiaryEntryCommand(commented, "Ótimo começo! Mantenha o café da manhã assim."));
    }

    /// <summary>
    /// More of the dietitian's patients, each at a different point of the follow-up: Daniela (vegetarian) keeps a full
    /// week of diary with entries waiting for comments; Eduardo was just assessed and his plan is still a draft; Fernanda's
    /// follow-up is overdue, with an ended first plan and a diary that stopped; Gabriel has not accepted the invitation yet.
    /// Skipped when Daniela's account already exists.
    /// </summary>
    private async Task SeedNutritionPatientsAsync(string password, Func<string, string, string> Email, CancellationToken cancellationToken)
    {
        if (await FindAccountAsync(Email("ClientDaniela", "daniela@myhealth.dev")) is not null)
        {
            return;
        }

        var dietitianUser = await FindAccountAsync(Email("Dietitian", "nutri@myhealth.dev"));
        var trainerUser = await FindAccountAsync(Email("Trainer", "treinador@myhealth.dev"));
        var tenant = Guid.Empty;
        if (trainerUser is not null)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            tenant = await db.Tenants.Where(t => t.CreatedBy == trainerUser.Id && t.Name == "Estúdio Movimento").Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
        }

        if (dietitianUser is null || tenant == Guid.Empty)
        {
            logger.LogWarning("The seeded dietitian or organization is missing; nutrition patients were not seeded.");
            return;
        }

        var dietitian = new Account(dietitianUser.Id, dietitianUser.Email!, "Nina Nutricionista");
        var daniela = await AccountAsync(Email("ClientDaniela", "daniela@myhealth.dev"), "Daniela Rocha", password);
        var eduardo = await AccountAsync(Email("ClientEduardo", "eduardo@myhealth.dev"), "Eduardo Pereira", password);
        var fernanda = await AccountAsync(Email("ClientFernanda", "fernanda@myhealth.dev"), "Fernanda Costa", password);
        var gabriel = await AccountAsync(Email("ClientGabriel", "gabriel@myhealth.dev"), "Gabriel Alves", password);
        foreach (var person in new[] { daniela, eduardo, fernanda, gabriel })
        {
            await RunAsync(person, null, new CompleteProfileCommand(person.Name, TimeZone));
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Daniela: vegetarian and lactose intolerant, a week of diary, two entries commented and the rest waiting.
        var danielaLink = await LinkAsync(dietitian, tenant, daniela);
        await RunAsync(dietitian, tenant, new RecordNutritionAssessmentCommand(daniela.Id, new NutritionAssessmentInput(
            today.AddDays(-20), "Ganhar massa magra sem comer carne", "Vegetariana (come ovos)", "Intolerância à lactose",
            "Gosta de grão-de-bico, tofu e frutas vermelhas; não gosta de berinjela", "Acorda 7h, almoça 12h, lancha 16h, janta 20h",
            [
                new MeasurementInput("body_mass", "scale", "kg", 58.2m), new MeasurementInput("height", "stadiometer", "cm", 165m),
                new MeasurementInput("body_fat", "bioimpedance", "%", 27.5m), new MeasurementInput("waist_circumference", "tape", "cm", 72m),
            ],
            "Diário diário por 4 semanas; retorno com nova bioimpedância.", today.AddDays(10), "Exames de sangue recentes sem alterações; vitamina B12 no limite inferior.")));
        var danielaPlan = await MealPlanAsync(dietitian, tenant, daniela, new MealPlanDetailsInput(
            "Vegetariano — hipertrofia", "Ganhar massa magra", today.AddDays(-19), today.AddDays(40), "Bebidas vegetais enriquecidas com cálcio. Suplementar B12 conforme orientação."),
        [
            new PlannedMealInput(null, "Café da manhã", new TimeOnly(7, 30), null,
            [
                new MealItemInput(null, "Aveia em flocos", 40m, "g", "3 colheres de sopa", null, ["Granola sem açúcar (40 g)"]),
                new MealItemInput(null, "Bebida de soja", 200m, "ml", null, "Enriquecida com cálcio", ["Bebida de aveia (200 ml)"]),
                new MealItemInput(null, "Frutas vermelhas", 100m, "g", "1 xícara", null, ["Banana (1 unidade)"]),
            ]),
            new PlannedMealInput(null, "Almoço", new TimeOnly(12, 0), null,
            [
                new MealItemInput(null, "Arroz integral", 120m, "g", "5 colheres de sopa", null, ["Quinoa (120 g)"]),
                new MealItemInput(null, "Grão-de-bico", 100m, "g", "1 concha", null, ["Lentilha (1 concha)", "Feijão (1 concha)"]),
                new MealItemInput(null, "Tofu grelhado", 100m, "g", null, null, ["Ovos (2 unidades)"]),
                new MealItemInput(null, "Legumes refogados", null, null, "À vontade", "Com azeite", null),
            ]),
            new PlannedMealInput(null, "Lanche", new TimeOnly(16, 0), null,
            [new MealItemInput(null, "Pasta de amendoim", 20m, "g", "1 colher de sopa", null, null), new MealItemInput(null, "Pão integral", null, null, "1 fatia", null, null)]),
            new PlannedMealInput(null, "Jantar", new TimeOnly(20, 0), null,
            [
                new MealItemInput(null, "Omelete", null, null, "2 ovos", "Com espinafre", ["Tofu mexido (100 g)"]),
                new MealItemInput(null, "Batata-doce", 100m, "g", null, null, ["Mandioca (100 g)"]),
                new MealItemInput(null, "Salada de folhas", null, null, "À vontade", null, null),
            ]),
        ], cancellationToken: cancellationToken);
        var danielaComments = new List<(Guid Entry, string Message)>();
        for (var day = 7; day >= 1; day--)
        {
            var breakfast = await MealAsync(daniela, danielaPlan, today, day, "Café da manhã", 7, MealAdherence.AsPlanned);
            if (day == 7)
            {
                danielaComments.Add((breakfast, "Perfeito! A aveia com frutas é uma ótima base para o dia."));
            }

            if (day % 3 == 0)
            {
                var lunch = await MealAsync(daniela, danielaPlan, today, day, "Almoço", 12, MealAdherence.Different,
                    "Self-service: arroz, salada e omelete; não tinha grão-de-bico.", "Difícil achar proteína vegetal perto do trabalho.");
                if (day == 6)
                {
                    danielaComments.Add((lunch, "Boa troca! Leve grão-de-bico cozido de casa nos dias de escritório."));
                }
            }
            else
            {
                await MealAsync(daniela, danielaPlan, today, day, "Almoço", 12, MealAdherence.AsPlanned);
            }

            if (day is 5 or 2)
            {
                await MealAsync(daniela, danielaPlan, today, day, "Lanche", 16, MealAdherence.Partly, "Só a pasta de amendoim, sem o pão.");
            }

            if (day == 4)
            {
                await MealAsync(daniela, danielaPlan, today, day, "Jantar", 21, MealAdherence.Unplanned, "Hambúrguer vegetariano com batata frita.", "Aniversário de uma amiga.");
            }
            else if (day != 1)
            {
                await MealAsync(daniela, danielaPlan, today, day, "Jantar", 20, MealAdherence.AsPlanned);
            }

            await WaterAsync(daniela, danielaLink, today, day, 1500 + (day % 3 * 250));
        }

        foreach (var (entry, message) in danielaComments)
        {
            await RunAsync(dietitian, tenant, new CommentOnFoodDiaryEntryCommand(entry, message));
        }

        // Eduardo: assessed three days ago (pre-diabetes); the dietitian is still writing his plan.
        await LinkAsync(dietitian, tenant, eduardo);
        await RunAsync(dietitian, tenant, new RecordNutritionAssessmentCommand(eduardo.Id, new NutritionAssessmentInput(
            today.AddDays(-3), "Controlar a glicemia e perder 8 kg", "Pré-diabetes; pouco açúcar e farinha branca", null,
            "Gosta de churrasco e de café; come fora quase todos os dias", "Café 8h, almoço 13h no restaurante, janta 21h",
            [
                new MeasurementInput("body_mass", "scale", "kg", 94.3m), new MeasurementInput("height", "stadiometer", "cm", 178m),
                new MeasurementInput("waist_circumference", "tape", "cm", 104m),
            ],
            "Plano focado em refeições fora de casa; retorno em 3 semanas.", today.AddDays(18), "Glicemia de jejum de 112 mg/dl em exame recente trazido pelo paciente.")));
        await MealPlanAsync(dietitian, tenant, eduardo, new MealPlanDetailsInput(
            "Controle glicêmico", "Glicemia de jejum abaixo de 100 mg/dl", today.AddDays(1), null, null),
        [
            new PlannedMealInput(null, "Café da manhã", new TimeOnly(8, 0), null,
            [new MealItemInput(null, "Ovos mexidos", 2m, "unidade", null, null, null), new MealItemInput(null, "Café sem açúcar", 150m, "ml", null, null, null)]),
            new PlannedMealInput(null, "Almoço (restaurante)", new TimeOnly(13, 0), "Monte o prato: metade salada, um quarto proteína, um quarto carboidrato.",
            [
                new MealItemInput(null, "Salada crua", null, null, "Meio prato", null, null),
                new MealItemInput(null, "Carne grelhada", 150m, "g", "1 bife médio", null, ["Frango grelhado (150 g)", "Peixe (150 g)"]),
                new MealItemInput(null, "Arroz integral", null, null, "3 colheres de sopa", null, null),
            ]),
        ], publish: false, cancellationToken: cancellationToken);

        // Fernanda: first plan ended, the second one stopped being followed and her follow-up is two weeks overdue.
        var fernandaLink = await LinkAsync(dietitian, tenant, fernanda);
        await RunAsync(dietitian, tenant, new RecordNutritionAssessmentCommand(fernanda.Id, new NutritionAssessmentInput(
            today.AddDays(-75), "Emagrecer para o casamento", null, "Camarão", "Gosta de massas e de doces", "Horários irregulares, trabalha em turnos",
            [new MeasurementInput("body_mass", "scale", "kg", 79.0m), new MeasurementInput("waist_circumference", "tape", "cm", 92m)],
            "Retorno em 30 dias.", today.AddDays(-45), null)));
        var firstPlan = await MealPlanAsync(dietitian, tenant, fernanda, new MealPlanDetailsInput(
            "Fase 1 — adaptação", "Organizar os horários", today.AddDays(-74), today.AddDays(-46), null),
        [
            new PlannedMealInput(null, "Refeição 1", null, "Ao acordar, qualquer turno.",
            [new MealItemInput(null, "Iogurte natural", 170m, "g", null, null, null), new MealItemInput(null, "Mamão", null, null, "1 fatia", null, null)]),
            new PlannedMealInput(null, "Refeição principal", null, null,
            [new MealItemInput(null, "Prato feito", null, null, "Metade salada, um quarto arroz e feijão, um quarto proteína", null, null)]),
        ], cancellationToken: cancellationToken);
        await RunAsync(dietitian, tenant, new EndMealPlanCommand(firstPlan.Id));
        await RunAsync(dietitian, tenant, new RecordNutritionAssessmentCommand(fernanda.Id, new NutritionAssessmentInput(
            today.AddDays(-45), "Emagrecer para o casamento", null, "Camarão", "Gosta de massas e de doces", "Turnos fixos agora: 7h às 15h",
            [new MeasurementInput("body_mass", "scale", "kg", 76.4m), new MeasurementInput("waist_circumference", "tape", "cm", 88m)],
            "Perdeu 2,6 kg. Retorno em 30 dias.", today.AddDays(-15), "Relata ansiedade com a data do casamento.")));
        var fernandaPlan = await MealPlanAsync(dietitian, tenant, fernanda, new MealPlanDetailsInput(
            "Fase 2 — déficit leve", "Chegar a 72 kg", today.AddDays(-44), null, "Uma refeição livre por semana."),
        [
            new PlannedMealInput(null, "Café da manhã", new TimeOnly(6, 30), null,
            [new MealItemInput(null, "Pão integral", null, null, "1 fatia", null, null), new MealItemInput(null, "Queijo branco", 30m, "g", "1 fatia", null, null)]),
            new PlannedMealInput(null, "Almoço", new TimeOnly(15, 30), null,
            [
                new MealItemInput(null, "Macarrão integral", 80m, "g", "1 pegador", null, ["Arroz integral (4 colheres de sopa)"]),
                new MealItemInput(null, "Molho de tomate caseiro", null, null, "2 colheres de sopa", null, null),
                new MealItemInput(null, "Frango desfiado", 100m, "g", null, null, null),
            ]),
            new PlannedMealInput(null, "Jantar", new TimeOnly(20, 0), null,
            [new MealItemInput(null, "Sopa de legumes", 300m, "ml", "1 prato fundo", null, null)]),
        ], cancellationToken: cancellationToken);
        await MealAsync(fernanda, fernandaPlan, today, 12, "Café da manhã", 6, MealAdherence.AsPlanned);
        await MealAsync(fernanda, fernandaPlan, today, 12, "Almoço", 15, MealAdherence.AsPlanned);
        await MealAsync(fernanda, fernandaPlan, today, 11, "Almoço", 16, MealAdherence.Partly, "Macarrão comum, não achei o integral.");
        await MealAsync(fernanda, fernandaPlan, today, 10, "Jantar", 22, MealAdherence.Unplanned, "Brigadeiros e lasanha.", "Semana muito difícil no trabalho.");
        await WaterAsync(fernanda, fernandaLink, today, 10, 800);

        // Gabriel: invited, has not answered yet.
        await RunAsync(dietitian, tenant, new InviteClientCommand(Discipline.Dietitian, gabriel.Email));

        logger.LogInformation(
            "Nutrition patients seeded for {Dietitian}: {Daniela}, {Eduardo}, {Fernanda} and a pending invitation to {Gabriel}.",
            dietitian.Email, daniela.Email, eduardo.Email, fernanda.Email, gabriel.Email);
    }

    /// <summary>
    /// Data for the professionals' AI assistant: Ana, Daniela and Fernanda allow the dietitian's AI analysis (Eduardo does
    /// not), and a therapist qualified in acupuncture (enabled here, Development only) joins the organization with Ana (care
    /// plan and two documented sessions), Carla (no plan yet) and Bruno (no AI). Skipped when the therapist's account exists.
    /// </summary>
    private async Task SeedAssistantAsync(string password, Func<string, string, string> Email, CancellationToken cancellationToken)
    {
        if (await FindAccountAsync(Email("Therapist", "terapeuta@myhealth.dev")) is not null)
        {
            return;
        }

        var accounts = new Dictionary<string, Account>();
        foreach (var (key, fallback, name) in new[]
                 {
                     ("Operator", "operador@myhealth.dev", "Olga Operadora"), ("Trainer", "treinador@myhealth.dev", "Tina Treinadora"),
                     ("Dietitian", "nutri@myhealth.dev", "Nina Nutricionista"), ("ClientAna", "ana@myhealth.dev", "Ana Souza"),
                     ("ClientBruno", "bruno@myhealth.dev", "Bruno Lima"), ("ClientCarla", "carla@myhealth.dev", "Carla Mendes"),
                     ("ClientDaniela", "daniela@myhealth.dev", "Daniela Rocha"), ("ClientFernanda", "fernanda@myhealth.dev", "Fernanda Costa"),
                 })
        {
            if (await FindAccountAsync(Email(key, fallback)) is not { } user)
            {
                logger.LogWarning("Seed account {Key} is missing; assistant data was not seeded.", key);
                return;
            }

            accounts[key] = new Account(user.Id, user.Email!, name, IsOperator: key == "Operator");
        }

        var (@operator, trainer, dietitian) = (accounts["Operator"], accounts["Trainer"], accounts["Dietitian"]);
        // Acupuncture unless Seed:Development:TherapyModalityCode names another (the seed test uses its own, created here).
        var modalityCode = configuration["Seed:Development:TherapyModalityCode"] ?? TherapyModality.Acupuncture;
        Guid tenant;
        Guid dietitianProfile;
        TherapyModality? modality;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            tenant = await db.Tenants.Where(t => t.CreatedBy == trainer.Id && t.Name == "Estúdio Movimento").Select(t => t.Id).FirstAsync(cancellationToken);
            dietitianProfile = await db.ProfessionalProfiles
                .Where(p => p.UserId == dietitian.Id && p.Discipline == Discipline.Dietitian).Select(p => p.Id).FirstAsync(cancellationToken);
            modality = await db.TherapyModalities.AsNoTracking().FirstOrDefaultAsync(m => m.Code == modalityCode, cancellationToken);
        }

        // The dietitian's assistant: three patients allow AI analysis.
        foreach (var key in new[] { "ClientAna", "ClientDaniela", "ClientFernanda" })
        {
            var client = accounts[key];
            Guid? relationship;
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                relationship = await db.CareRelationships
                    .Where(r => r.TenantId == tenant && r.ClientId == client.Id && r.ProfessionalProfileId == dietitianProfile && r.Status == RelationshipStatus.Active)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (relationship is { } id)
            {
                await RunAsync(client, null, new ChangeDataSharingCommand(id, DataCategory.AiCoaching, Shared: true));
            }
        }

        // Acupuncture is seeded disabled until its legal review; Development enables it so the therapy screens have data.
        int consentVersion;
        if (modality is not { Status: ModalityStatus.Enabled })
        {
            var input = new ModalityInput(
                "Acupuntura", "Prática integrativa com agulhas em pontos específicos.", ["COFFITO", "CFM", "CRF", "COFEN"],
                TherapyModality.AcupunctureDefaultRequiredFields, RequiresConsent: true);
            if (modality is null)
            {
                await RunAsync(@operator, null, new DefineModalityCommand(modalityCode, input));
            }
            else
            {
                await RunAsync(@operator, null, new ConfigureModalityCommand(modalityCode, input));
            }

            consentVersion = await RunAsync(@operator, null, new PublishConsentTemplateCommand(
                modalityCode, "Termo de consentimento — acupuntura", "Declaro que fui informado(a) sobre o procedimento, seus benefícios e riscos.",
                ["Dor local", "Pequeno sangramento ou hematoma", "Desmaio (raro)"]));
            await RunAsync(@operator, null, new ChangeModalityStatusCommand(modalityCode, Enable: true, "Seed de desenvolvimento"));
        }
        else
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            consentVersion = await db.ConsentTemplates.Where(t => t.ModalityId == modality.Id).MaxAsync(t => t.Version, cancellationToken);
        }

        // The therapist: verified, qualified in acupuncture and a member of the organization.
        var therapist = await AccountAsync(Email("Therapist", "terapeuta@myhealth.dev"), "Teresa Terapeuta", password);
        await RunAsync(therapist, null, new CompleteProfileCommand(therapist.Name, TimeZone));
        var therapistProfile = await RunAsync(therapist, null, new CreateProfessionalProfileCommand(
            Discipline.TherapyPractitioner, "Teresa Terapeuta", "Acupuntura para dor e bem-estar.", null, new RegistrationInput("COFFITO", "123456-F", "SP")));
        await RunAsync(@operator, null, new VerifyProfessionalCommand(therapistProfile, "Seed de desenvolvimento"));
        var membership = await RunAsync(trainer, tenant, new InviteMemberCommand(therapist.Email, [Discipline.TherapyPractitioner], IsAdministrator: false));
        await RunAsync(therapist, null, new RespondToMembershipInvitationCommand(membership, InvitationResponse.Accept));
        var qualification = await RunAsync(therapist, null, new SubmitQualificationCommand(
            modalityCode, "Especialização em Acupuntura", "Instituto Brasileiro de Acupuntura", "ACU-2020-115", new DateOnly(2020, 12, 10)));
        await RunAsync(@operator, null, new DecideQualificationCommand(qualification, QualificationDecision.Verify, "Seed de desenvolvimento"));
        var service = await RunAsync(therapist, tenant, new OfferTherapyServiceCommand(
            modalityCode, new TherapyServiceInput("Sessão de acupuntura", null, 50, ServiceDelivery.InPerson)));

        // Ana: consent, a care plan for low back pain and two documented sessions she rated; one more scheduled.
        var ana = accounts["ClientAna"];
        var anaLink = await LinkAsync(therapist, tenant, ana, Discipline.TherapyPractitioner);
        await RunAsync(ana, null, new ChangeDataSharingCommand(anaLink, DataCategory.AiCoaching, Shared: true));
        await RunAsync(ana, null, new DecideConsentCommand(anaLink, modalityCode, consentVersion, ConsentDecision.Granted));
        await RunAsync(therapist, tenant, new StartCarePlanCommand(
            ana.Id, modalityCode, "Reduzir a dor lombar após os treinos de força e melhorar o sono.", 8, "1 vez por semana",
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(10)));
        var now = clock.GetUtcNow();
        foreach (var (daysAgo, concern, wellbeing, comments) in new[]
                 {
                     (14, "Dor lombar 6/10 depois do treino de terra; dorme mal.", 3, "Senti alívio no dia seguinte."),
                     (7, "Dor lombar 4/10; sono melhor.", 4, "Dormi bem a semana toda."),
                 })
        {
            var startsAt = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-daysAgo).AddHours(13), TimeSpan.Zero);
            var session = await RunAsync(therapist, tenant, new ScheduleTherapySessionCommand(ana.Id, service, startsAt));
            await RunAsync(therapist, tenant, new DocumentTherapySessionCommand(session, new TherapyRecordInput(
                concern, "Tensão em paravertebrais lombares, sem sinais de alerta.", "Sem anticoagulantes; sem gestação.", "Acupuntura sistêmica",
                "B23, B25, B40, VG4", 40, "Nenhum", "Alongamentos leves e compressa morna em casa.")));
            await RunAsync(therapist, tenant, new CompleteTherapySessionCommand(session));
            await RunAsync(ana, null, new ReportTherapyProgressCommand(session, wellbeing, comments));
        }

        await RunAsync(therapist, tenant, new ScheduleTherapySessionCommand(ana.Id, service, now.AddDays(3)));

        // Carla allows AI but has no plan yet (ask the assistant for one); Bruno does not allow AI.
        var carla = accounts["ClientCarla"];
        var carlaLink = await LinkAsync(therapist, tenant, carla, Discipline.TherapyPractitioner);
        await RunAsync(carla, null, new ChangeDataSharingCommand(carlaLink, DataCategory.AiCoaching, Shared: true));
        await LinkAsync(therapist, tenant, accounts["ClientBruno"], Discipline.TherapyPractitioner);

        logger.LogInformation("Assistant data seeded: therapist {Therapist} with patients {Ana}, {Carla} and {Bruno}.", therapist.Email, ana.Email, carla.Email, accounts["ClientBruno"].Email);
    }

    private async Task<Guid> LinkAsync(Account professional, Guid tenant, Account client, Discipline discipline = Discipline.Dietitian)
    {
        var link = await RunAsync(professional, tenant, new InviteClientCommand(discipline, client.Email));
        await RunAsync(client, null, new RespondToInvitationCommand(link, InvitationResponse.Accept));
        return link;
    }

    private sealed record SeededMealPlan(Guid Id, IReadOnlyDictionary<string, Guid> Meals);

    /// <summary>Creates a meal plan with its meals and, unless <paramref name="publish"/> is false, publishes its first version.</summary>
    private async Task<SeededMealPlan> MealPlanAsync(
        Account dietitian, Guid tenant, Account client, MealPlanDetailsInput details, IReadOnlyList<PlannedMealInput> meals, bool publish = true,
        CancellationToken cancellationToken = default)
    {
        var planId = await RunAsync(dietitian, tenant, new CreateMealPlanCommand(client.Id, details));
        await RunAsync(dietitian, tenant, new UpdateMealPlanMealsCommand(planId, meals));
        if (publish)
        {
            await RunAsync(dietitian, tenant, new PublishMealPlanCommand(planId, "Primeira versão do plano"));
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.MealPlans.AsNoTracking().SingleAsync(p => p.Id == planId, cancellationToken);
        return new SeededMealPlan(planId, plan.Draft.Meals.ToDictionary(m => m.Name, m => m.Id));
    }

    /// <summary>A diary entry for one of the plan's meals, eaten <paramref name="daysAgo"/> days ago at <paramref name="hour"/> (São Paulo time).</summary>
    private async Task<Guid> MealAsync(
        Account client, SeededMealPlan plan, DateOnly today, int daysAgo, string meal, int hour, MealAdherence adherence, string? description = null,
        string? notes = null)
    {
        var eatenAt = new DateTimeOffset(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(-3));
        var entryId = Guid.CreateVersion7(eatenAt);
        await RunAsync(client, null, new SaveFoodDiaryEntryCommand(
            entryId, plan.Id, plan.Meals[meal], null, null, eatenAt, TimeZone, description, null, adherence, notes));
        return entryId;
    }

    /// <summary>The day's water, shared with the dietitian through the relationship.</summary>
    private Task WaterAsync(Account client, Guid relationshipId, DateOnly today, int daysAgo, int milliliters)
    {
        var drankAt = new DateTimeOffset(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(21, 0)), TimeSpan.FromHours(-3));
        return RunAsync(client, null, new SaveFoodDiaryEntryCommand(
            Guid.CreateVersion7(drankAt), null, null, relationshipId, "Água", drankAt, TimeZone, null, milliliters, null, null));
    }

    /// <summary>
    /// A sample every 30 seconds following the workout's sets (warm-up, effort, recovery), with a 3-minute gap where the
    /// watch lost contact and one value the client typed in.
    /// </summary>
    private async Task HeartRateAsync(Account client, Guid workoutId, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt, endedAt;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var session = await db.WorkoutSessions.AsNoTracking().SingleAsync(s => s.Id == workoutId, cancellationToken);
            (startedAt, endedAt) = (session.StartedAt, session.CompletedAt!.Value);
        }

        var points = new List<HeartRatePointInput>();
        var count = (int)((endedAt - startedAt).TotalSeconds / 30);
        for (var i = 1; i < count; i++)
        {
            var minute = i / 2.0;
            if (minute is > 14 and < 17)
            {
                continue;
            }

            var bpm = 88 + (40 * Math.Min(1, minute / 6)) + (18 * Math.Sin(minute / 1.6)) - (minute > count / 2.0 - 4 ? 25 : 0);
            points.Add(new HeartRatePointInput(Guid.CreateVersion7(), startedAt.AddSeconds(i * 30), Math.Round(bpm), "Apple Watch", "Apple Watch Series 9"));
        }

        points.Add(new HeartRatePointInput(Guid.CreateVersion7(), startedAt.AddMinutes(15.5), 150, "MyHealth", null, WasUserEntered: true));
        await RunAsync(client, null, new SyncWorkoutHeartRateCommand(workoutId, startedAt, endedAt, points));
    }

    private static PlannedSessionInput Strength(string name, params PlannedExerciseInput[] exercises) =>
        new(null, name, SessionKind.Strength, null, exercises, null);

    private static PlannedExerciseInput Reps(Guid exerciseId, int sets, int min, int max, decimal? loadKg, int restSeconds) =>
        new(null, exerciseId, null, restSeconds, [], [.. Enumerable.Range(0, sets).Select(_ => new SetTargetInput(min, max, null, loadKg))]);

    private static PlannedExerciseInput Timed(Guid exerciseId, int sets, int seconds, int restSeconds) =>
        new(null, exerciseId, "Mantenha o abdômen contraído.", restSeconds, [], [.. Enumerable.Range(0, sets).Select(_ => new SetTargetInput(null, null, seconds, null))]);
}
