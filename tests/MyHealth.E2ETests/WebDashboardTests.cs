namespace MyHealth.E2ETests;

/// <summary>The professionals' web dashboard (MyHealth.Web), signed in as the seeded trainer of "Estúdio Movimento".</summary>
public sealed class WebDashboardTests(AppFixture app) : BrowserTest(app)
{
    [Fact]
    public async Task Signing_in_opens_the_page_that_was_asked_for_without_errors()
    {
        await Page.GotoAsync(new Uri(App.Web, "trainer").ToString());
        await Page.WaitForURLAsync(url => url.Contains("/login", StringComparison.OrdinalIgnoreCase));

        await Page.Locator("#email").FillAsync(AppFixture.Trainer);
        await Page.Locator("#password").FillAsync(AppFixture.Password);
        await Button("Entrar").ClickAsync();

        await Page.WaitForURLAsync(url => url.EndsWith("/trainer", StringComparison.Ordinal));
        await WaitForPageAsync("Clientes");
    }

    [Fact]
    public async Task A_wrong_password_is_refused_with_a_message()
    {
        await Page.GotoAsync(new Uri(App.Web, "login").ToString());
        await Page.Locator("#email").FillAsync(AppFixture.Trainer);
        await Page.Locator("#password").FillAsync("wrong-password-1");
        await Button("Entrar").ClickAsync();

        await ExpectTextAsync("E-mail ou senha incorretos.");
        Page.Url.ShouldContain("/login");
    }

    [Fact]
    public async Task The_only_organization_is_selected_and_its_menu_shown_right_after_signing_in()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await WaitForPageAsync("Olá, Tina");

        await Assertions.Expect(Page.Locator("header select")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^[0-9a-f-]{36}$"));
        await Assertions.Expect(Page.Locator("header select option:checked")).ToHaveTextAsync("Estúdio Movimento");
        foreach (var item in new[] { "Clientes", "Biblioteca de exercícios", "Membros", "Auditoria", "Plano e cobrança", "Segurança" })
        {
            await Assertions.Expect(Page.Locator("nav").GetByRole(AriaRole.Link, new() { Name = item, Exact = true })).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task Every_dashboard_page_opens_without_errors()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);

        await OpenAsync(App.Web, "", "Olá, Tina");
        await OpenAsync(App.Web, "trainer", "Clientes");
        await ExpectTextAsync("Ana Souza");
        await ExpectTextAsync("Bruno Lima");
        await OpenAsync(App.Web, "trainer/exercises", "Biblioteca de exercícios");
        await ExpectTextAsync("Agachamento no banco");
        await OpenAsync(App.Web, "organization/members", "Membros");
        await ExpectTextAsync("Nina Nutricionista");
        await OpenAsync(App.Web, "organization/audit", "Auditoria");
        await OpenAsync(App.Web, "organization/billing", "Plano e cobrança");
        await OpenAsync(App.Web, "account/security", "Verificação em duas etapas");
    }

    [Fact]
    public async Task The_trainer_reviews_a_client_applies_the_ai_coach_suggestion_and_publishes()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer", "Clientes");

        await Link("Ana Souza").ClickAsync();
        await WaitForPageAsync("Ana Souza");
        await ExpectTextAsync("Força — fase 1");
        await Button("Ver").First.ClickAsync();
        await ExpectTextAsync("Feedback do cliente");

        await Link("Força — fase 1").ClickAsync();
        await WaitForPageAsync("Força — fase 1");
        await ExpectTextAsync("Agachamento livre");

        await Button("Analisar progresso").ClickAsync();
        await ExpectTextAsync("Ajustar carga — Agachamento livre");
        await ExpectTextAsync("Nova carga: 42 kg em todas as séries");
        await Button("Aplicar ao rascunho").First.ClickAsync();
        await ExpectTextAsync("Sugestão aplicada ao rascunho");

        await Button("Publicar…").ClickAsync();
        await Page.Locator("#change-description").FillAsync("Carga do agachamento ajustada");
        await Button("Publicar agora").ClickAsync();
        await ExpectTextAsync("Versão 2 publicada");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_trainer_builds_and_publishes_a_new_plan_with_the_editor()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer", "Clientes");
        await Link("Bruno Lima").ClickAsync();
        await WaitForPageAsync("Bruno Lima");

        await Button("Novo plano").ClickAsync();
        await Page.Locator("#plan-name").FillAsync("Plano criado no teste");
        await Page.Locator("#plan-goal").FillAsync("Testar o editor de ponta a ponta");
        await Button("Criar e montar treinos").ClickAsync();
        await WaitForPageAsync("Plano criado no teste");

        // Editing the plan's details (open for a new plan) enables saving.
        await Page.Locator("#details-notes").FillAsync("Aquecer 5 minutos antes.");
        await Assertions.Expect(Button("Salvar rascunho")).ToBeEnabledAsync();

        await Button("+ Treino de musculação").First.ClickAsync();
        await Page.Locator("#picker-search").FillAsync("Supino");
        await Assertions.Expect(Button("Adicionar")).ToHaveCountAsync(1); // The search (debounced) narrowed the list.
        await Button("Adicionar").First.ClickAsync();
        await ExpectTextAsync("Supino reto adicionado a Treino A.");

        await Button("Salvar rascunho").ClickAsync();
        await ExpectTextAsync("Rascunho salvo.");
        await Button("Publicar…").ClickAsync();
        await Assertions.Expect(Page.Locator("#change-description")).ToHaveValueAsync("Primeira versão do plano");
        await Button("Publicar agora").ClickAsync();
        await ExpectTextAsync("Versão 1 publicada");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_assistant_shows_every_student_summarizes_them_and_suggests_how_to_evolve_exercises()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await Page.Locator("nav").GetByRole(AriaRole.Link, new() { Name = "Assistente IA", Exact = true }).ClickAsync();
        await WaitForPageAsync("Assistente IA");

        // Every student, with summaries; Bruno has not allowed AI analysis.
        await ExpectTextAsync("Ana Souza");
        await ExpectTextAsync("Força — fase 1 · v");
        await Assertions.Expect(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Bruno Lima" })).ToBeDisabledAsync();
        await ExpectTextAsync("sem IA");

        // A summary of everyone comes back with real names.
        await Button("Resumo da semana de todos").ClickAsync();
        await Assertions.Expect(Page.Locator("section[aria-label='Conversa com o assistente']")).ToContainTextAsync("Ana Souza");
        await ExpectTextAsync("não autorizaram a análise por IA");

        // Focused on Ana: a suggestion to evolve her squat, applied to her plan's draft.
        await Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Ana Souza" }).First.ClickAsync();
        await ExpectTextAsync("Conversando sobre");
        await Button("Como evoluir os exercícios de Ana Souza?").ClickAsync();
        await ExpectTextAsync("Ajustar carga — Agachamento livre");
        await Button("Aplicar ao rascunho").First.ClickAsync();
        await ExpectTextAsync("aplicada");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_trainer_sees_the_heart_rate_a_client_shares()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer", "Clientes");
        await Link("Ana Souza").ClickAsync();
        await WaitForPageAsync("Ana Souza");

        await ExpectTextAsync("Frequência cardíaca");
        await Button("Ver gráfico").First.ClickAsync();
        var chart = Page.Locator("figure svg[role=img]");
        await Assertions.Expect(chart).ToBeVisibleAsync();
        await Assertions.Expect(chart.Locator("polyline")).ToHaveCountAsync(2); // The 3-minute gap breaks the line.
        await ExpectTextAsync("informado pelo cliente");
        await ExpectTextAsync("1 trecho sem medição (linha interrompida)");
        await Page.Locator("figure").ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "TestResults", "heart-rate-chart.png") });
        await AssertHealthyAsync();

        // Bruno does not share his heart rate.
        await OpenAsync(App.Web, "trainer", "Clientes");
        await Link("Bruno Lima").ClickAsync();
        await WaitForPageAsync("Bruno Lima");
        await ExpectTextAsync("O cliente não compartilha a frequência cardíaca com você.");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_trainer_answers_a_discomfort_report()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer", "Clientes");
        await Link("Bruno Lima").ClickAsync();
        await WaitForPageAsync("Bruno Lima");

        await Button("Ver").First.ClickAsync();
        await ExpectTextAsync("Leve dor no ombro direito no supino.");
        await Page.Locator("textarea[id^='reply-']").FillAsync("Vamos reduzir a carga do supino e conversar na próxima aula.");
        await Button("Enviar resposta").ClickAsync();
        await ExpectTextAsync("Sua resposta em");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_trainer_adds_an_exercise_and_opens_the_3d_video_panel()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer/exercises", "Biblioteca de exercícios");

        await Button("Novo exercício").ClickAsync();
        await Page.Locator("#ex-name").FillAsync("Afundo com halteres");
        await Page.Locator("#ex-description").FillAsync("Passo à frente, descer até os dois joelhos formarem 90 graus.");
        await Page.Locator("#ex-muscles").FillAsync("quadríceps, glúteos");
        await Button("Salvar").ClickAsync();
        await ExpectTextAsync("Afundo com halteres");

        await Page.Locator("tr").Filter(new() { HasText = "Afundo com halteres" }).GetByRole(AriaRole.Button, new() { Name = "Vídeo 3D" }).ClickAsync();
        await ExpectTextAsync("Vídeo 3D — Afundo com halteres");
        await ExpectTextAsync("nunca clientes");
        await AssertHealthyAsync();
    }
}
