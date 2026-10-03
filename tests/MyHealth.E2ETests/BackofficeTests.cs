namespace MyHealth.E2ETests;

/// <summary>The operators' back office (MyHealth.Backoffice), signed in as the seeded platform operator.</summary>
public sealed class BackofficeTests(AppFixture app) : BrowserTest(app)
{
    [Fact]
    public async Task Every_back_office_page_opens_without_errors()
    {
        await SignInAsync(App.Backoffice, AppFixture.Operator);

        await OpenAsync(App.Backoffice, "", "Cobrança");
        await OpenAsync(App.Backoffice, "billing/tenants", "Assinaturas");
        await ExpectTextAsync("Estúdio Movimento");
        await OpenAsync(App.Backoffice, "billing/prices", "Preços dos módulos");
        await OpenAsync(App.Backoffice, "admin/tenants", "Organizações");
        await ExpectTextAsync("Estúdio Movimento");
        await OpenAsync(App.Backoffice, "admin/professionals", "Verificação de profissionais");
        await OpenAsync(App.Backoffice, "admin/qualifications", "Qualificações de terapeutas");
        await OpenAsync(App.Backoffice, "admin/modalities", "Modalidades de terapia");
        await OpenAsync(App.Backoffice, "admin/privacy-requests", "Solicitações de titulares (LGPD)");
        await OpenAsync(App.Backoffice, "admin/retention", "Políticas de retenção");
        await OpenAsync(App.Backoffice, "admin/audit", "Auditoria de acessos");
        await OpenAsync(App.Backoffice, "admin/operators", "Operadores da plataforma");
        await ExpectTextAsync(AppFixture.Operator);
        await OpenAsync(App.Backoffice, "account/security", "Verificação em duas etapas");
    }

    [Fact]
    public async Task The_operator_opens_an_organization_subscription()
    {
        await SignInAsync(App.Backoffice, AppFixture.Operator);
        await OpenAsync(App.Backoffice, "billing/tenants", "Assinaturas");

        await Link("Estúdio Movimento").First.ClickAsync();
        await WaitForPageAsync("Estúdio Movimento");
        await ExpectTextAsync("Período de teste");
    }

    [Fact]
    public async Task Only_operators_can_sign_in()
    {
        await Page.GotoAsync(new Uri(App.Backoffice, "login").ToString());
        await Page.Locator("#email").FillAsync(AppFixture.Trainer);
        await Page.Locator("#password").FillAsync(AppFixture.Password);
        await Button("Entrar").ClickAsync();

        await ExpectTextAsync("Acesso restrito aos operadores da plataforma.");
        Page.Url.ShouldContain("/login");
    }
}
