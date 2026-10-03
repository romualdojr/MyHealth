extern alias web;

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Infrastructure.Identity;
using web::MyHealth.Web.Common.Api;
using web::MyHealth.Web.Common.Training;
using Web = web::MyHealth.Web.Common.Api;

namespace MyHealth.Api.IntegrationTests;

/// <summary>
/// The web dashboard against the real API: it signs in with real Identity bearer tokens (including the two-factor step)
/// and reads every endpoint its pages use with its own client and models, so a renamed field or route fails here.
/// </summary>
public sealed class WebDashboardContractTests(ApiFactory factory) : IntegrationTest(factory)
{
    private const string Password = "Correct-horse-9";

    private sealed class TestServerClients(ApiFactory factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory.CreateClient();
    }

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private IHttpClientFactory Clients => new TestServerClients(Scenario.Factory);

    /// <summary>A real account with an authenticator enabled, optionally a platform operator.</summary>
    private async Task<(string Email, ApplicationUser User)> AccountAsync(string name, bool administrator)
    {
        Scenario.Factory.EnsureAvailable();
        var email = $"{name}-{Guid.NewGuid():N}@test.local";
        var anonymous = Scenario.Factory.CreateClient();
        await Scenario.EnsureSuccess(await anonymous.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password }, Ct));

        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
        if (administrator)
        {
            await users.AddToRoleAsync(user, Roles.Administrator);
        }

        return (email, user);
    }

    /// <summary>
    /// The 6-digit code an authenticator app would show now (RFC 6238: HMAC-SHA1 over the 30-second step, keyed with the
    /// base32 shared key). Identity's own provider cannot generate it: authenticator codes are made on the phone.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6238 TOTP (and ASP.NET Core Identity) use HMAC-SHA1; test code only.")]
    private async Task<string> TotpAsync(ApplicationUser user)
    {
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var key = Base32Decode((await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(user.Id.ToString()))!))!);

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = System.Security.Cryptography.HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var buffer = 0;
        var bytes = new List<byte>();
        foreach (var c in value.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    private async Task<(MyHealthApiClient Api, ApiSession Session, ClaimsPrincipal Principal)> SignedInAsync(string name, bool administrator)
    {
        var (email, user) = await AccountAsync(name, administrator);
        var signIn = new SignInService(Clients, TimeProvider.System);

        (await signIn.SignInAsync(email, Password, null, Ct)).Outcome.ShouldBe(SignInOutcome.RequiresTwoFactor);
        (await signIn.SignInAsync(email, "wrong-password", await TotpAsync(user), Ct)).Outcome.ShouldBe(SignInOutcome.Failed);
        var result = await signIn.SignInAsync(email, Password, await TotpAsync(user), Ct);
        result.Outcome.ShouldBe(SignInOutcome.Succeeded);

        var session = new ApiSession(new FixedAuthentication(result.Principal!), Clients, TimeProvider.System);
        return (new MyHealthApiClient(Clients, session), session, result.Principal!);
    }

    private static async Task<T> Ok<T>(Task<ApiResult<T>> call)
        where T : class
    {
        var result = await call;
        result.Problem.ShouldBeNull();
        return result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_operator_signs_in_with_two_factor_and_every_admin_page_reads_its_data()
    {
        var (api, _, principal) = await SignedInAsync("operator", administrator: true);
        principal.IsInRole(SessionClaims.AdministratorRole).ShouldBeTrue();
        principal.FindFirstValue(SessionClaims.HasProfile).ShouldBeNull();

        // Some content for the lists to show.
        var (pending, _) = await Scenario.ProfessionalAsync("pending", verified: false);
        var client = await Scenario.ClientAsync();
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync("/api/v1/me/privacy-requests", new { type = "Correction", details = "Corrigir peso" }, Ct));

        (await Ok(api.GetAsync<List<ProfessionalProfileItem>>("admin/professional-profiles?status=Pending", ct: Ct))).ShouldContain(p => p.UserId == pending.Id);
        await Ok(api.GetAsync<List<QualificationItem>>("admin/practitioner-qualifications", ct: Ct));
        (await Ok(api.GetAsync<List<ModalityItem>>("admin/therapy-modalities", ct: Ct))).ShouldContain(m => m.Code == "acupuncture" && m.Status == "Disabled");
        (await Ok(api.GetAsync<List<PrivacyRequestItem>>("admin/privacy-requests?status=Submitted", ct: Ct))).ShouldContain(r => r.UserId == client.Id && r.Type == "Correction");
        (await Ok(api.GetAsync<List<RetentionPolicyItem>>("admin/retention-policies", ct: Ct))).ShouldContain(p => p.RecordType == "AuditEntry");
        (await Ok(api.GetAsync<Web.PagedResponse<AuditEntryItem>>("admin/audit-entries?page=1&pageSize=50", ct: Ct))).Items.ShouldNotBeEmpty();
        (await Ok(api.GetAsync<List<TenantSummaryItem>>("admin/tenants", ct: Ct))).ShouldNotBeEmpty();
        await Ok(api.GetAsync<List<AdministratorItem>>("admin/administrators", ct: Ct));
        (await Ok(api.PostAsync<TwoFactorStatus>("auth/manage/2fa", new { }, ct: Ct))).IsTwoFactorEnabled.ShouldBeTrue();

        // Actions the pages perform, with the bodies they send.
        var pendingProfile = (await Ok(api.GetAsync<List<ProfessionalProfileItem>>("admin/professional-profiles?status=Pending", ct: Ct))).First(p => p.UserId == pending.Id);
        (await api.PostAsync($"admin/professional-profiles/{pendingProfile.Id}/reject", new { reason = "Registro não confere" }, ct: Ct)).Problem.ShouldBeNull();
        (await api.PutAsync("admin/retention-policies/FoodDiaryEntry", new { retentionMonths = 60, legalBasis = "Tutela da saúde", notes = (string?)null }, ct: Ct)).Problem.ShouldBeNull();

        // A problem comes back in plain words with its code.
        var invalid = await api.PostAsync("admin/therapy-modalities/acupuncture/enable", new { legalReviewReference = "Parecer 1" }, ct: Ct);
        invalid.Problem.ShouldNotBeNull().Status.ShouldBe(400);
        invalid.Problem.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_trainer_reads_their_workspace_dashboard_and_organization_through_the_selected_tenant()
    {
        var (api, session, _) = await SignedInAsync("trainer", administrator: false);
        var (operatorApi, _, _) = await SignedInAsync("operator", administrator: true);

        await Scenario.EnsureSuccess(await Fail(api.PostAsync("me/profile", new { fullName = "Tina Treinadora", timeZoneId = "America/Sao_Paulo" }, ct: Ct)));
        var profileId = (await Ok(api.PostAsync<Scenario.IdResponse>("professional-profiles", new
        {
            discipline = "PersonalTrainer", displayName = "Tina", registration = new { council = "CREF", number = "123456", region = "SP" },
        }, ct: Ct))).Id;
        (await operatorApi.PostAsync($"admin/professional-profiles/{profileId}/verify", new { notes = "ok" }, ct: Ct)).Problem.ShouldBeNull();
        var tenantId = (await Ok(api.PostAsync<Scenario.IdResponse>("tenants", new { name = "Estúdio Tina", kind = "SoloPractice", disciplines = new[] { "PersonalTrainer" } }, ct: Ct))).Id;

        var me = await Ok(api.GetAsync<MeResponse>("me", ct: Ct));
        me.ProfessionalProfiles.ShouldHaveSingleItem().VerificationStatus.ShouldBe("Verified");
        var membership = me.Memberships.ShouldHaveSingleItem();
        (membership.TenantId, membership.IsAdministrator).ShouldBe((tenantId, true));
        membership.Disciplines.ShouldBe(["PersonalTrainer"]);

        // Without a selected organization the professional endpoints refuse; with it they answer.
        (await api.GetAsync<TrainerDashboard>("trainer/dashboard", ct: Ct)).Problem.ShouldNotBeNull().Status.ShouldBe(400);
        session.TenantId = tenantId;
        (await Ok(api.GetAsync<TrainerDashboard>("trainer/dashboard", ct: Ct))).Clients.ShouldBeEmpty();
        (await Ok(api.GetAsync<List<MembershipItem>>("tenant/memberships", ct: Ct))).ShouldHaveSingleItem().IsAdministrator.ShouldBeTrue();
        await Ok(api.GetAsync<Web.PagedResponse<AuditEntryItem>>("tenant/audit-entries?page=1&pageSize=50", ct: Ct));

        // Invite as the members page does.
        (await api.PostAsync<object>("tenant/memberships/invitations", new { email = "colega@test.local", disciplines = new[] { "Dietitian" }, isAdministrator = false }, ct: Ct))
            .Problem.ShouldBeNull();
        (await Ok(api.GetAsync<List<MembershipItem>>("tenant/memberships", ct: Ct))).ShouldContain(m => m.Email == "colega@test.local" && m.Status == "Pending");

        // A non-operator gets a clear 403 from the admin area.
        (await api.GetAsync<List<TenantSummaryItem>>("admin/tenants", ct: Ct)).Problem.ShouldNotBeNull().Status.ShouldBe(403);
    }

    [Fact]
    public async Task The_back_office_billing_pages_and_the_organization_billing_page_read_and_act_on_their_data()
    {
        var (operatorApi, _, _) = await SignedInAsync("operator", administrator: true);
        var (owner, _) = await Scenario.ProfessionalAsync("owner");
        var tenantId = owner.TenantId!.Value;

        // Back office: overview, subscriptions, prices, a tenant's detail and the actions its page performs.
        (await Ok(operatorApi.GetAsync<List<Web.ModulePriceItem>>("admin/billing/module-prices", ct: Ct))).Select(p => p.Module).ShouldBe(["Training", "Nutrition", "Therapies"]);
        (await Ok(operatorApi.GetAsync<List<Web.TenantBillingItem>>("admin/billing/tenants?status=Trial", ct: Ct))).ShouldContain(t => t.TenantId == tenantId);
        (await operatorApi.PutAsync($"admin/billing/tenants/{tenantId}/modules", new { modules = new[] { "Training", "Nutrition" }, reason = "Upgrade" }, ct: Ct)).Problem.ShouldBeNull();
        (await operatorApi.PostAsync($"admin/billing/tenants/{tenantId}/trial", new { until = DateTimeOffset.UtcNow.AddDays(60), reason = "Negociação" }, ct: Ct)).Problem.ShouldBeNull();
        (await operatorApi.PostAsync($"admin/billing/tenants/{tenantId}/invoices", ct: Ct)).Problem.ShouldBeNull();
        var detail = await Ok(operatorApi.GetAsync<Web.TenantBillingDetail>($"admin/billing/tenants/{tenantId}", ct: Ct));
        detail.Summary.Status.ShouldBe("Active");
        detail.Summary.Modules.ShouldBe(["Training", "Nutrition"]);
        var invoice = detail.Invoices.ShouldHaveSingleItem();
        invoice.Lines.Select(l => l.Module).ShouldBe(["Training", "Nutrition"]);
        detail.History.Select(h => h.Status).ShouldContain("Trial");

        // Organization: its administrator sees the invoice and gets a Pix charge; the operator simulates the payment.
        var mine = await Scenario.Read<Web.TenantBillingDetail>(await owner.Http.GetAsync("/api/v1/tenant/billing", Ct));
        mine.Invoices.ShouldHaveSingleItem().Status.ShouldBe("Open");
        (await Scenario.Read<Web.ChargeResponse>(await owner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoice.Id}/charge", null, Ct))).PixCopyPaste.ShouldNotBeNull();
        (await Ok(operatorApi.PostAsync<Web.WebhookResult>($"admin/billing/invoices/{invoice.Id}/simulate-payment", null, ct: Ct))).Outcome.ShouldBe("applied");
        (await Ok(operatorApi.PostAsync<Web.BillingCycleResult>("admin/billing/run-cycle", null, ct: Ct))).Subscriptions.ShouldBeGreaterThan(0);

        var summary = await Ok(operatorApi.GetAsync<Web.RevenueSummary>("admin/billing/summary", ct: Ct));
        summary.TenantsByStatus.ShouldContainKey("Active");
        summary.RecentPayments.ShouldContain(p => p.InvoiceId == invoice.Id && p.TenantName == detail.Summary.Name);
        (await Scenario.Read<Web.MeResponse>(await owner.Http.GetAsync("/api/v1/me", Ct))).Memberships.Single(m => m.TenantId == tenantId).SubscriptionStatus.ShouldBe("Active");
    }

    [Fact]
    public async Task A_trainer_builds_publishes_and_follows_up_a_plan_with_the_web_editor()
    {
        var (api, session, _) = await SignedInAsync("builder", administrator: false);
        var (operatorApi, _, _) = await SignedInAsync("operator", administrator: true);
        (await api.PostAsync("me/profile", new { fullName = "Bruno Treinador", timeZoneId = "America/Sao_Paulo" }, ct: Ct)).Problem.ShouldBeNull();
        var profileId = (await Ok(api.PostAsync<Web.IdResponse>("professional-profiles", new
        {
            discipline = "PersonalTrainer", displayName = "Bruno", registration = new { council = "CREF", number = "654321", region = "SP" },
        }, ct: Ct))).Id;
        (await operatorApi.PostAsync($"admin/professional-profiles/{profileId}/verify", new { notes = "ok" }, ct: Ct)).Problem.ShouldBeNull();
        session.TenantId = (await Ok(api.PostAsync<Web.IdResponse>("tenants", new { name = "Estúdio Bruno", kind = "SoloPractice", disciplines = new[] { "PersonalTrainer" } }, ct: Ct))).Id;

        // Clients page: invite two clients, who accept in the app.
        var ana = await Scenario.ClientAsync("ana");
        var caio = await Scenario.ClientAsync("caio");
        var relationships = new Dictionary<Guid, Guid>();
        foreach (var client in new[] { ana, caio })
        {
            var invitation = await Ok(api.PostAsync<Web.IdResponse>("care-relationships/invitations", new { discipline = "PersonalTrainer", clientEmail = client.Email }, ct: Ct));
            await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/care-relationships/{invitation.Id}/accept", null, Ct));
            relationships[client.Id] = invitation.Id;
        }

        (await Ok(api.GetAsync<TrainerDashboard>("trainer/dashboard", ct: Ct))).Clients.Select(c => c.ClientId).ShouldBe([ana.Id, caio.Id], ignoreOrder: true);

        // Library page: a custom exercise next to the curated ones; the picker's filters.
        (await Ok(api.PostAsync<Web.IdResponse>("exercises", new
        {
            name = "Agachamento no banco", description = "Sentar e levantar do banco sem apoio das mãos.", muscleGroups = new[] { "quadríceps" },
            equipment = new[] { "banco" }, purpose = "força", precautions = (string?)null, mediaCaption = "Pessoa senta e levanta do banco sem usar as mãos.",
        }, ct: Ct))).Id.ShouldNotBe(Guid.Empty);
        var custom = (await Ok(api.GetAsync<Web.PagedResponse<ExerciseItem>>("exercises?pageSize=20&search=agachamento no banco&muscleGroup=&equipment=", ct: Ct))).Items.ShouldHaveSingleItem();
        custom.IsCustom.ShouldBeTrue();

        // 3D panel: terms, upload through the upload client (raw video body), the worker, review and accept.
        var terms = await Ok(api.GetAsync<MotionCaptureTerms>("motion-capture/terms", ct: Ct));
        using (var video = new MemoryStream([0, 0, 0, 24, 102, 116, 121, 112, 109, 112, 52, 50]))
        {
            var job = await Ok(api.PostFileAsync<MotionCaptureJobItem>(
                $"exercises/{custom.Id}/motion-captures?fileName=banco.mp4&durationSeconds=6&consentVersion={terms.Version}", video, "video/mp4", video.Length, Ct));
            job.Status.ShouldBe("Queued");
            await using (var scope = Scenario.Factory.Services.CreateAsyncScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<MyHealth.Application.MotionCapture.MotionCaptureProcessor>();
                while (await processor.ProcessDueAsync(Ct) > 0)
                {
                }
            }

            var ready = (await Ok(api.GetAsync<List<MotionCaptureJobItem>>($"exercises/{custom.Id}/motion-captures", ct: Ct))).ShouldHaveSingleItem();
            (ready.Status, ready.SourceDeleted).ShouldBe(("Succeeded", true));
            ready.Outputs.ShouldContain(o => o.Kind == "Model3D" && o.Format == "glb");
            (await Ok(api.PostAsync<MotionCaptureJobItem>($"motion-captures/{job.Id}/accept", null, ct: Ct))).AcceptedAt.ShouldNotBeNull();
        }

        (await Ok(api.GetAsync<Web.PagedResponse<ExerciseItem>>("exercises?pageSize=20&search=agachamento no banco", ct: Ct))).Items.Single().Media
            .Select(m => m.Kind).ShouldBe(["Loop", "Loop", "Loop", "Model3D"]);
        var library = (await Ok(api.GetAsync<Web.PagedResponse<ExerciseItem>>("exercises?pageSize=20&search=supino", ct: Ct))).Items[0];
        library.IsCustom.ShouldBeFalse();

        // Client page: a new plan; the editor fills it with the same bodies the screens send.
        var details = new PlanDetailsForm { Name = "Força — fase 1", Goal = "Ganhar força", Level = "Beginner", StartDate = new DateOnly(2026, 10, 5), SuggestedSessionsPerWeek = 3 };
        var planId = (await Ok(api.PostAsync<Web.IdResponse>("training-plans", new { clientId = ana.Id, details = details.ToBody() }, ct: Ct))).Id;

        var plan = await Ok(api.GetAsync<TrainerPlan>($"training-plans/{planId}", ct: Ct));
        (plan.Status, plan.CurrentVersionNumber, plan.Details.Name).ShouldBe(("Draft", 0, "Força — fase 1"));
        var draft = PlanDraft.From(plan.Draft);
        draft.PublishingIssues().ShouldNotBeEmpty();

        var strength = draft.AddSession("Strength");
        strength.Name.ShouldBe("Treino A");
        strength.Exercises.Add(ExerciseDraft.New(library));
        var plank = ExerciseDraft.New(custom);
        plank.Sets.Clear();
        plank.AddSet();
        plank.Sets[0].ByDuration = true;
        plank.Sets[0].DurationSeconds = 45;
        strength.Exercises.Add(plank);
        strength.Exercises.Move(plank, -1);
        draft.AddSession("Aerobic").Aerobic!.DistanceKm = 3m;
        draft.PublishingIssues().ShouldBeEmpty();

        var edited = PlanDetailsForm.From(plan.Details);
        edited.Notes = "Beba água";
        (await api.PutAsync($"training-plans/{planId}", edited.ToBody(), ct: Ct)).Problem.ShouldBeNull();
        (await api.PutAsync($"training-plans/{planId}/sessions", draft.ToBody(), ct: Ct)).Problem.ShouldBeNull();

        plan = await Ok(api.GetAsync<TrainerPlan>($"training-plans/{planId}", ct: Ct));
        plan.Details.Notes.ShouldBe("Beba água");
        var saved = PlanDraft.From(plan.Draft);
        saved.Sessions.Select(s => (s.Name, s.Kind)).ShouldBe([("Treino A", "Strength"), ("Aeróbico 1", "Aerobic")]);
        saved.Sessions[0].Exercises.Select(e => e.ExerciseName).ShouldBe([custom.Name, library.Name]);
        saved.Sessions[0].Exercises[0].Sets.ShouldHaveSingleItem().DurationSeconds.ShouldBe(45);
        saved.Sessions[0].Exercises[1].Sets.Select(s => (s.MinRepetitions, s.MaxRepetitions)).ShouldBe([(10, 12), (10, 12), (10, 12)]);
        saved.Sessions[1].Aerobic!.DistanceKm.ShouldBe(3m);

        // Saving again without changes keeps every id, so the editor sees no unsaved changes after a reload.
        (await api.PutAsync($"training-plans/{planId}/sessions", saved.ToBody(), ct: Ct)).Problem.ShouldBeNull();
        PlanDraft.From((await Ok(api.GetAsync<TrainerPlan>($"training-plans/{planId}", ct: Ct))).Draft).Snapshot().ShouldBe(saved.Snapshot());

        var published = await Ok(api.PostAsync<PublishedVersion>($"training-plans/{planId}/publish", new { changeDescription = "Primeira versão do plano" }, ct: Ct));
        published.VersionNumber.ShouldBe(1);
        var item = (await Ok(api.GetAsync<List<TrainerPlanItem>>($"training-plans?clientId={ana.Id}", ct: Ct))).ShouldHaveSingleItem();
        (item.Status, item.CurrentVersionNumber, item.HasUnpublishedChanges).ShouldBe(("Published", 1, false));
        (await Ok(api.GetAsync<TrainerPlan>($"training-plans/{planId}", ct: Ct))).Versions.ShouldHaveSingleItem().ChangeDescription.ShouldBe("Primeira versão do plano");

        // The client works out and reports discomfort; the trainer reads the workout and replies.
        var (workoutId, _) = await Scenario.StartAsync(ana, planId);
        await Scenario.CompleteAsync(ana, workoutId, discomfort: true);
        var workout = (await Ok(api.GetAsync<List<WorkoutSessionItem>>($"trainer/clients/{ana.Id}/workout-sessions", ct: Ct))).ShouldHaveSingleItem();
        (workout.Status, workout.ReportedDiscomfort, workout.HasReply, workout.PlannedSessionName).ShouldBe(("Completed", true, false, "Treino A"));
        var detail = await Ok(api.GetAsync<WorkoutSessionDetail>($"workout-sessions/{workoutId}", ct: Ct));
        detail.Feedback.ShouldNotBeNull().DiscomfortDetails.ShouldBe("Dor no joelho");
        (await api.PostAsync($"workout-sessions/{workoutId}/feedback-reply", new { message = "Vamos reduzir a carga do agachamento." }, ct: Ct)).Problem.ShouldBeNull();
        (await Ok(api.GetAsync<WorkoutSessionDetail>($"workout-sessions/{workoutId}", ct: Ct))).Reply.ShouldNotBeNull().Message.ShouldBe("Vamos reduzir a carga do agachamento.");

        // AI coach panel: the client allows AI analysis; after the discomfort report the coach proposes a conversation, not a progression.
        (await Ok(api.GetAsync<CoachOverview>($"training-plans/{planId}/coach", ct: Ct))).SharingGranted.ShouldBeFalse();
        await Scenario.EnsureSuccess(await ana.Http.PutAsync($"/api/v1/care-relationships/{relationships[ana.Id]}/sharing/AiCoaching", null, Ct));
        var analysis = await Ok(api.PostAsync<CoachAnalysisItem>($"training-plans/{planId}/coach/analyses", new { focus = "joelho" }, ct: Ct));
        var talk = analysis.Suggestions.ShouldHaveSingleItem();
        (talk.Kind, talk.Status, talk.Title).ShouldBe(("TalkToClient", "Pending", "Conversar com o cliente"));
        analysis.Notices.ShouldNotBeEmpty();
        (await Ok(api.PostAsync<CoachAnalysisItem>($"coach-analyses/{analysis.Id}/suggestions/{talk.Id}/apply", null, ct: Ct))).Suggestions[0].Status.ShouldBe("Applied");
        var coach = await Ok(api.GetAsync<CoachOverview>($"training-plans/{planId}/coach", ct: Ct));
        (coach.SharingGranted, coach.UsedToday, coach.Analyses[0].Focus).ShouldBe((true, 1, "joelho"));

        // Client page: the WhatsApp button appears once the client confirms a number and allows professionals to contact them.
        (await api.GetAsync<WhatsAppLink>($"care-relationships/{relationships[ana.Id]}/whatsapp", ct: Ct)).Problem.ShouldNotBeNull().Status.ShouldBe(404);
        await Scenario.EnsureSuccess(await ana.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = "(11) 97654-3219", allowProfessionalContact = true }, Ct));
        var whatsApp = Scenario.Factory.Services.GetRequiredService<MyHealth.Infrastructure.Messaging.FakeWhatsAppProvider>();
        var code = whatsApp.Sent.Last(m => m.PhoneNumber == "+5511976543219").Parameters[0];
        await Scenario.EnsureSuccess(await ana.Http.PostAsJsonAsync("/api/v1/me/whatsapp/confirm", new { code }, Ct));
        (await Ok(api.GetAsync<WhatsAppLink>($"care-relationships/{relationships[ana.Id]}/whatsapp", ct: Ct))).ChatUrl.ShouldBe("https://wa.me/5511976543219");

        // Copy to another client, then end the original.
        var copyId = (await Ok(api.PostAsync<Web.IdResponse>($"training-plans/{planId}/copy", new { targetClientId = caio.Id, newName = "Força — Caio" }, ct: Ct))).Id;
        var copy = await Ok(api.GetAsync<TrainerPlan>($"training-plans/{copyId}", ct: Ct));
        (copy.ClientId, copy.Status, copy.Details.Name, copy.Draft.Sessions.Count).ShouldBe((caio.Id, "Draft", "Força — Caio", 2));
        (await api.PostAsync($"training-plans/{planId}/end", ct: Ct)).Problem.ShouldBeNull();
        (await Ok(api.GetAsync<TrainerPlan>($"training-plans/{planId}", ct: Ct))).Status.ShouldBe("Ended");
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_transparently()
    {
        var (email, user) = await AccountAsync("refresh", administrator: false);
        var signIn = await new SignInService(Clients, TimeProvider.System).SignInAsync(email, Password, await TotpAsync(user), Ct);
        var claims = signIn.Principal!.Claims
            .Where(c => c.Type != SessionClaims.ExpiresAt)
            .Append(new Claim(SessionClaims.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture)));
        var expired = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var session = new ApiSession(new FixedAuthentication(expired), Clients, TimeProvider.System);

        var token = await session.GetAccessTokenAsync(Ct);

        token.ShouldNotBeNull();
        token.ShouldNotBe(signIn.Principal.FindFirstValue(SessionClaims.AccessToken));
        (await new MyHealthApiClient(Clients, session).PostAsync<TwoFactorStatus>("auth/manage/2fa", new { }, ct: Ct)).Problem.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("admin/audit", "/admin/audit")]
    [InlineData("/trainer?x=1", "/trainer?x=1")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void Return_urls_never_leave_the_site(string? returnUrl, string expected) => ReturnUrls.Sanitize(returnUrl).ShouldBe(expected);

    private static async Task<HttpResponseMessage> Fail(Task<ApiResult<NoContent>> call)
    {
        var result = await call;
        return new HttpResponseMessage(result.IsSuccess ? HttpStatusCode.OK : (HttpStatusCode)result.Problem!.Status)
        {
            Content = new StringContent(result.Problem?.Message ?? string.Empty),
        };
    }
}
