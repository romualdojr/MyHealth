namespace MyHealth.E2ETests;

/// <summary>
/// The dietitian's workspace on the web dashboard, signed in as the seeded dietitian of "Estúdio Movimento", who follows
/// Ana Souza (assessment, published meal plan and a few days of diary).
/// </summary>
public sealed class NutritionWorkspaceTests(AppFixture app) : BrowserTest(app)
{
    private ILocator Tab(string name) => Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });

    private async Task OpenAnaAsync()
    {
        await SignInAsync(App.Web, AppFixture.Dietitian);
        await Page.Locator("nav").GetByRole(AriaRole.Link, new() { Name = "Pacientes", Exact = true }).ClickAsync();
        await WaitForPageAsync("Pacientes");
        await Link("Ana Souza").ClickAsync();
        await WaitForPageAsync("Ana Souza");
    }

    [Fact]
    public async Task Every_nutrition_page_opens_without_errors()
    {
        await SignInAsync(App.Web, AppFixture.Dietitian);

        await OpenAsync(App.Web, "dietitian", "Pacientes");
        await ExpectTextAsync("Reeducação alimentar");
        await ExpectTextAsync("como planejado");

        await Link("Ana Souza").ClickAsync();
        await WaitForPageAsync("Ana Souza");
        await ExpectTextAsync("publicada"); // Version 1, or 2 once the editor test has run.
        await Tab("Avaliações").ClickAsync();
        await ExpectTextAsync("Amendoim");
        await Tab("Diário").ClickAsync();
        await ExpectTextAsync("Pizza com amigos.");
        await ExpectTextAsync("Ótimo começo! Mantenha o café da manhã assim.");
        await AssertHealthyAsync();

        await Tab("Plano alimentar").ClickAsync();
        await Link("Reeducação alimentar").ClickAsync();
        await WaitForPageAsync("Reeducação alimentar");
        await Assertions.Expect(Page.Locator("#food-0-0")).ToHaveValueAsync("Pão integral");
        await ExpectTextAsync("Alergias:");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_dietitian_adds_a_meal_and_publishes_a_new_version()
    {
        await OpenAnaAsync();
        await Link("Reeducação alimentar").ClickAsync();
        await WaitForPageAsync("Reeducação alimentar");

        // The plan has four meals; the next meal of the usual day is offered with an empty food line.
        await Button("+ Adicionar refeição").ClickAsync();
        await Assertions.Expect(Page.Locator("#meal-name-4")).ToHaveValueAsync("Lanche da manhã");
        await Page.Locator("#food-4-0").FillAsync("Castanhas");

        // Without a portion the editor says what to fix instead of sending it.
        await Button("Salvar rascunho").ClickAsync();
        await ExpectTextAsync("Lanche da manhã · Castanhas: informe a quantidade com unidade ou uma medida caseira.");
        await Page.Locator("#measure-4-0").FillAsync("1 punhado");
        await Button("Salvar rascunho").ClickAsync();
        await ExpectTextAsync("Rascunho salvo.");

        // Saved meals are listed by time: the morning snack is now the second meal.
        await Assertions.Expect(Page.Locator("#meal-name-1")).ToHaveValueAsync("Lanche da manhã");

        await Button("Publicar…").ClickAsync();
        await Page.Locator("#change-description").FillAsync("Lanche da manhã incluído");
        await Button("Publicar agora").ClickAsync();
        await ExpectTextAsync("Versão 2 publicada");
        await ExpectTextAsync("Lanche da manhã incluído");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_dietitian_comments_on_a_diary_entry()
    {
        await OpenAnaAsync();
        await Tab("Diário").ClickAsync();

        var entry = Page.Locator("article").Filter(new() { HasText = "Pizza com amigos." });
        await Assertions.Expect(entry).ToContainTextAsync("fora do plano");
        await Assertions.Expect(entry).ToContainTextAsync("Fiquei com muita fome depois do treino.");
        await entry.GetByRole(AriaRole.Button, new() { Name = "Comentar", Exact = true }).ClickAsync();
        await entry.Locator("textarea").FillAsync("Tudo bem! Vamos reforçar o lanche pré-treino para chegar com menos fome.");
        await Button("Enviar comentário").ClickAsync();

        await Assertions.Expect(entry).ToContainTextAsync("Vamos reforçar o lanche pré-treino");
        await Assertions.Expect(entry).ToContainTextAsync("Nina Nutricionista");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_dietitian_records_a_new_assessment_from_the_latest_one()
    {
        await OpenAnaAsync();
        await Tab("Avaliações").ClickAsync();
        await ExpectTextAsync("Peso: 68,5 kg");

        await Button("Nova avaliação").ClickAsync();
        // What the patient reported last time comes pre-filled for the dietitian to confirm.
        await Assertions.Expect(Page.Locator("#new-assessment-allergies")).ToHaveValueAsync("Amendoim");
        await Page.Locator("#new-assessment-body_mass").FillAsync("67.9");
        await Page.Locator("#new-assessment-followup").FillAsync("Manter o plano; retorno em 30 dias.");
        await Button("Registrar avaliação").ClickAsync();

        await ExpectTextAsync("Avaliação registrada. O paciente foi avisado.");
        await ExpectTextAsync("Peso: 67,9 kg");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_diary_shows_the_week_of_the_plan_against_what_was_logged()
    {
        await OpenAnaAsync();
        await Tab("Diário").ClickAsync();

        var grid = Page.Locator("table").Filter(new() { HasText = "Refeição" }).First;
        await ExpectTextAsync("Plano × registros da semana — Reeducação alimentar");
        await Assertions.Expect(grid.GetByRole(AriaRole.Rowheader, new() { Name = "Café da manhã" })).ToBeVisibleAsync();
        await Assertions.Expect(grid).ToContainTextAsync("como planejado");
        await Assertions.Expect(grid).ToContainTextAsync("em parte");
        await AssertHealthyAsync();
    }

    [Fact]
    public async Task The_dietitian_copies_a_plan_to_another_patient_and_duplicates_a_meal()
    {
        await OpenAnaAsync();
        await Page.Locator("div").Filter(new() { HasText = "Reeducação alimentar" }).GetByRole(AriaRole.Button, new() { Name = "Copiar para…" }).First.ClickAsync();
        await Page.Locator("#copy-target").SelectOptionAsync(new SelectOptionValue { Label = "Bruno Lima" });
        await Page.Locator("#copy-name").FillAsync("Reeducação — Bruno");
        await Button("Copiar").ClickAsync();

        await WaitForPageAsync("Reeducação — Bruno");
        await Assertions.Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Caminho" })).ToContainTextAsync("Bruno Lima");
        await ExpectTextAsync("O paciente ainda não vê este plano.");

        await Button("Duplicar").First.ClickAsync();
        await Assertions.Expect(Page.Locator("#meal-name-1")).ToHaveValueAsync("Café da manhã (cópia)");
        await Assertions.Expect(Page.Locator("#food-1-0")).ToHaveValueAsync("Pão integral");
        await Page.Locator("#meal-name-1").FillAsync("Café da manhã de domingo");
        await Page.Locator("#meal-time-1").FillAsync("09:00");
        await Button("Salvar rascunho").ClickAsync();
        await ExpectTextAsync("Rascunho salvo.");
        await AssertHealthyAsync();
    }
}
