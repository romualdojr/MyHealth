using MyHealth.Application.ProfessionalAssistant;

namespace MyHealth.Application.UnitTests.Coaching;

/// <summary>"Nenhum" in the mandatory adverse events field is not an event: it must not flag the patient or block a care plan.</summary>
public class TherapyAdverseEventTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Nenhum")]
    [InlineData("nenhuma.")]
    [InlineData("Não houve")]
    [InlineData("Sem intercorrências")]
    [InlineData("sem eventos adversos")]
    [InlineData("N/A")]
    [InlineData("-")]
    public void Means_none(string? text) => TherapyAssistantWorkspace.AdverseEvent(text).ShouldBeNull();

    [Theory]
    [InlineData("Pequeno hematoma no ponto B23")]
    [InlineData("Nenhum sangramento, mas tontura leve ao levantar")]
    [InlineData("Sem dor, porém hematoma")]
    public void Keeps_real_events(string text) => TherapyAssistantWorkspace.AdverseEvent(text).ShouldBe(text);
}
