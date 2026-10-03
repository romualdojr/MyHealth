using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.Exercises;
using MyHealth.Application.Professionals;
using MyHealth.Application.Tenants;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Users;
using MyHealth.Application.Wellbeing;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Plans;
using MyHealth.Infrastructure.Identity;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>
/// Development data so every screen has something to show: an operator, an organization with a verified trainer and a
/// dietitian, three clients (two linked, one invitation pending), published training plans and a few weeks of
/// workouts. Accounts and password are in <c>Seed:Development</c> (appsettings.Development.json).
/// <para>
/// Each step runs the same application command the API runs, as the person who would do it (a signed-in user and the
/// selected organization are set on the request context), so domain rules, trials, grants, versions and notifications
/// behave exactly as in real use. Runs once: skipped when the operator account already exists. Never enabled outside
/// Development.
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

        await using (var scope = services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(Email("Operator", "operador@myhealth.dev")) is not null)
            {
                logger.LogInformation("Development data already seeded.");
                return;
            }
        }

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
        foreach (var category in new[] { DataCategory.WorkoutSummary, DataCategory.Wellbeing, DataCategory.AiCoaching })
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
        await WorkoutAsync(ana, anaPlan, "Treino A", daysAgo: 2, exertion: 6, atTop: true, comments: "Fácil hoje!", cancellationToken: cancellationToken);
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

    private static PlannedSessionInput Strength(string name, params PlannedExerciseInput[] exercises) =>
        new(null, name, SessionKind.Strength, null, exercises, null);

    private static PlannedExerciseInput Reps(Guid exerciseId, int sets, int min, int max, decimal? loadKg, int restSeconds) =>
        new(null, exerciseId, null, restSeconds, [], [.. Enumerable.Range(0, sets).Select(_ => new SetTargetInput(min, max, null, loadKg))]);

    private static PlannedExerciseInput Timed(Guid exerciseId, int sets, int seconds, int restSeconds) =>
        new(null, exerciseId, "Mantenha o abdômen contraído.", restSeconds, [], [.. Enumerable.Range(0, sets).Select(_ => new SetTargetInput(null, null, seconds, null))]);
}
