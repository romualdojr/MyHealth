using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Assistant;
using MyHealth.Infrastructure.ClientExperience;

namespace MyHealth.Api.IntegrationTests;

/// <summary>The Claude client's wire format, checked against a stubbed Messages API (no key, no network).</summary>
public sealed class AnthropicAssistantModelTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AssistantContext Context = new("Ana", new DateOnly(2026, 10, 1), "12:48", [], null, null, null);

    private static AssistantModelRequest Request() => new(
        "rules",
        Context,
        "{\"today\":\"2026-10-01\"}",
        [new AssistantTurn(AssistantTurn.Assistant, "Oi, Ana!"), new AssistantTurn(AssistantTurn.User, "Oi"), new AssistantTurn(AssistantTurn.User, "Tudo bem?")],
        "Almocei arroz",
        AssistantTools.All);

    private static AnthropicAssistantModel Model(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") }, new ConfigurationBuilder().Build(), NullLogger<AnthropicAssistantModel>.Instance);

    [Fact]
    public async Task It_sends_the_rules_context_turns_and_tools_and_reads_text_and_tool_calls()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"content":[
              {"type":"text","text":"Anotei assim. Confere?"},
              {"type":"tool_use","id":"toolu_1","name":"log_meal","input":{"meal_name":"Almoço","adherence":"Partly"}}
            ]}
            """);

        var reply = await Model(handler).RespondAsync(Request(), TestContext.Current.CancellationToken);

        reply.ShouldNotBeNull().Text.ShouldBe("Anotei assim. Confere?");
        var call = reply.ToolCalls.ShouldHaveSingleItem();
        call.Name.ShouldBe("log_meal");
        call.Input.GetProperty("adherence").GetString().ShouldBe("Partly");

        handler.RequestUri!.AbsolutePath.ShouldBe("/v1/messages");
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var root = sent.RootElement;
        root.GetProperty("model").GetString().ShouldBe(AnthropicAssistantModel.DefaultModel);
        root.GetProperty("max_tokens").GetInt32().ShouldBe(1024);
        root.GetProperty("system").GetString().ShouldNotBeNull().ShouldContain("{\"today\":\"2026-10-01\"}");
        root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ShouldBe(["log_meal", "report_body_check", "log_set"]);
        root.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString().ShouldBe("object");

        // Turns alternate and start with the user: the leading assistant turn is dropped and consecutive user turns merged.
        var messages = root.GetProperty("messages").EnumerateArray().Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())).ToList();
        messages.ShouldBe([("user", "Oi\nTudo bem?\nAlmocei arroz")]);
    }

    [Fact]
    public async Task A_provider_error_means_unavailable()
    {
        var reply = await Model(new StubHandler(HttpStatusCode.TooManyRequests, "{}")).RespondAsync(Request(), TestContext.Current.CancellationToken);

        reply.ShouldBeNull();
    }
}
