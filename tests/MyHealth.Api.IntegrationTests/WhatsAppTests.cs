using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Abstractions.WhatsApp;
using MyHealth.Application.Messaging;
using MyHealth.Domain.Messaging;
using MyHealth.Infrastructure.Messaging;
using MyHealth.Infrastructure.Notifications;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Api.IntegrationTests;

/// <summary>WhatsApp through the generic provider interface (fake provider): opt-in with a code, notifications, click-to-chat, opt-out.</summary>
public sealed class WhatsAppTests(ApiFactory factory) : IntegrationTest(factory)
{
    private FakeWhatsAppProvider Provider => Scenario.Factory.Services.GetRequiredService<FakeWhatsAppProvider>();

    /// <summary>A Brazilian mobile number nobody else in the test run uses, as a person would type it.</summary>
    private static (string Typed, string E164) NewNumber()
    {
        var line = Random.Shared.Next(1000_0000, 9999_9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        line = line.EndsWith("0000", StringComparison.Ordinal) ? line[..^1] + "1" : line;
        return ($"(11) 9{line[..4]}-{line[4..]}", $"+55119{line}");
    }

    private string LastCode(string phone) =>
        Provider.Sent.Last(m => m.PhoneNumber == phone && m.Template == WhatsAppTemplates.VerificationCode).Parameters[0];

    private async Task<string> OptInAsync(TestUser user, bool allowProfessionalContact = true)
    {
        var (typed, phone) = NewNumber();
        var pending = await Scenario.Read<WhatsAppStatusResponse>(
            await user.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = typed, allowProfessionalContact }, Ct));
        (pending.PhoneNumber, pending.Status).ShouldBe((phone, WhatsAppContactStatus.PendingVerification));
        var active = await Scenario.Read<WhatsAppStatusResponse>(await user.Http.PostAsJsonAsync("/api/v1/me/whatsapp/confirm", new { code = LastCode(phone) }, Ct));
        active.Status.ShouldBe(WhatsAppContactStatus.Active);
        return phone;
    }

    [Fact]
    public async Task A_number_is_used_only_after_the_code_sent_to_it_is_confirmed()
    {
        var client = await Scenario.ClientAsync();
        var (typed, phone) = NewNumber();

        var pending = await Scenario.Read<WhatsAppStatusResponse>(
            await client.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = typed, allowProfessionalContact = false }, Ct));
        pending.Status.ShouldBe(WhatsAppContactStatus.PendingVerification);
        var code = LastCode(phone);
        code.Length.ShouldBe(6);

        // Asking again at once is refused; a wrong code is counted and refused.
        (await client.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = typed, allowProfessionalContact = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var wrong = await client.Http.PostAsJsonAsync("/api/v1/me/whatsapp/confirm", new { code = code == "000000" ? "111111" : "000000" }, Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("WhatsApp.WrongCode");

        var active = await Scenario.Read<WhatsAppStatusResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/whatsapp/confirm", new { code }, Ct));
        (active.Status, active.PhoneNumber).ShouldBe((WhatsAppContactStatus.Active, phone));
        active.VerifiedAt.ShouldNotBeNull();

        // Invalid numbers and numbers not on WhatsApp are refused without being recorded.
        (await client.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = "123", allowProfessionalContact = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.Http.PutAsJsonAsync("/api/v1/me/whatsapp", new { phoneNumber = "+55 11 91234-0000", allowProfessionalContact = false }, Ct)).IsSuccessStatusCode.ShouldBeFalse();
        (await Scenario.Read<WhatsAppStatusResponse>(await client.Http.GetAsync("/api/v1/me/whatsapp", Ct))).Status.ShouldBe(WhatsAppContactStatus.Active);

        // The opt-in (consent) is audited.
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditEntries.CountAsync(e => e.ActorUserId == client.Id && e.Action == "grant" && e.ResourceType == nameof(WhatsAppContact), Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Notifications_also_go_to_whatsapp_with_the_generic_text_only()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var phone = await OptInAsync(client, allowProfessionalContact: false);

        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        await using (var scope = Scenario.Factory.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
            while (await dispatcher.DispatchDueAsync(Ct) > 0)
            {
            }
        }

        var message = Provider.Sent.Last(m => m.PhoneNumber == phone && m.Template == WhatsAppTemplates.Notification);
        message.Language.ShouldBe("pt_BR");
        message.Parameters.ShouldBe(["Treino atualizado", "Seu profissional publicou uma nova versão do seu plano de treino."]);
    }

    [Fact]
    public async Task Professionals_get_a_chat_link_only_when_the_client_allows_it()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationshipId = await Scenario.LinkAsync(trainer, client);

        (await trainer.Http.GetAsync($"/api/v1/care-relationships/{relationshipId}/whatsapp", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var phone = await OptInAsync(client, allowProfessionalContact: true);
        var link = await Scenario.Read<ProfessionalWhatsAppResponse>(await trainer.Http.GetAsync($"/api/v1/care-relationships/{relationshipId}/whatsapp", Ct));
        (link.PhoneNumber, link.ChatUrl).ShouldBe((phone, $"https://wa.me/{phone[1..]}"));

        // Another organization's professional never sees it.
        var (other, _) = await Scenario.ProfessionalAsync("other");
        (await other.Http.GetAsync($"/api/v1/care-relationships/{relationshipId}/whatsapp", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/whatsapp/professional-contact", new { allow = false }, Ct));
        (await trainer.Http.GetAsync($"/api/v1/care-relationships/{relationshipId}/whatsapp", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replying_SAIR_opts_out_and_unsigned_webhooks_are_ignored()
    {
        var client = await Scenario.ClientAsync();
        var phone = await OptInAsync(client);
        var anonymous = Scenario.Factory.CreateClient();
        var body = JsonSerializer.Serialize(new { messages = new[] { new { id = Guid.NewGuid().ToString(), from = phone, text = "Sair", timestamp = DateTimeOffset.UtcNow } } });

        using (var unsigned = new StringContent(body, Encoding.UTF8, "application/json"))
        {
            (await anonymous.PostAsync("/api/v1/whatsapp/webhooks", unsigned, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await Scenario.Read<WhatsAppStatusResponse>(await client.Http.GetAsync("/api/v1/me/whatsapp", Ct))).Status.ShouldBe(WhatsAppContactStatus.Active);

        using var signed = new StringContent(body, Encoding.UTF8, "application/json");
        signed.Headers.Add(FakeWhatsAppProvider.SignatureHeader, Provider.Sign(body));
        var result = await Scenario.Read<WhatsAppWebhookResult>(await anonymous.PostAsync("/api/v1/whatsapp/webhooks", signed, Ct));
        (result.Messages, result.OptOuts).ShouldBe((1, 1));

        var status = await Scenario.Read<WhatsAppStatusResponse>(await client.Http.GetAsync("/api/v1/me/whatsapp", Ct));
        (status.Status, status.AllowProfessionalContact).ShouldBe((WhatsAppContactStatus.OptedOut, false));
        Provider.Sent.ShouldContain(m => m.PhoneNumber == phone && m.Template == WhatsAppTemplates.OptOutConfirmation);

        // The provider's subscription check.
        (await anonymous.GetStringAsync("/api/v1/whatsapp/webhooks?hub.mode=subscribe&hub.verify_token=myhealth-fake-verify-token&hub.challenge=42", Ct)).ShouldBe("42");
        (await anonymous.GetAsync("/api/v1/whatsapp/webhooks?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
