using MyHealth.Application.TrainerAssistant;

namespace MyHealth.Application.UnitTests.Coaching;

/// <summary>Students' names never reach the AI provider: they become aliases on the way out and names again on the way back.</summary>
public class StudentAliasesTests
{
    private static readonly StudentAliases Aliases = new([("Aluno 1", "Ana Souza"), ("Aluno 2", "Bruno Lima"), ("Aluno 3", "Ana Paula Reis"), ("Aluno 10", "Caio Prado")]);

    [Theory]
    [InlineData("Como está a Ana Souza?", "Como está a Aluno 1?")]
    [InlineData("compare ana souza e BRUNO LIMA", "compare Aluno 1 e Aluno 2")]
    [InlineData("E o Bruno, treinou?", "E o Aluno 2, treinou?")]
    [InlineData("Ana Paula Reis voltou", "Aluno 3 voltou")]
    [InlineData("Caio e Caiobá", "Aluno 10 e Caiobá")]
    public void Names_typed_by_the_trainer_become_aliases(string typed, string sent) => Aliases.Hide(typed).ShouldBe(sent);

    [Fact]
    public void A_first_name_shared_by_two_students_is_not_guessed()
    {
        // "Ana" alone could be Ana Souza or Ana Paula Reis: it is left as typed (the model then asks or answers generally).
        Aliases.Hide("Como está a Ana?").ShouldBe("Como está a Ana?");
    }

    [Fact]
    public void Aliases_in_the_answer_become_names_again_without_confusing_similar_aliases()
    {
        Aliases.Reveal("Aluno 1 progrediu; Aluno 10 faltou; aluno 2 relatou dor.")
            .ShouldBe("Ana Souza progrediu; Caio Prado faltou; Bruno Lima relatou dor.");
    }
}
