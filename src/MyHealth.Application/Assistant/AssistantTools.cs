using System.Text.Json;
using MyHealth.Application.Abstractions.Assistant;

namespace MyHealth.Application.Assistant;

/// <summary>The rules and tools given to the model. Tools only prepare suggestions; nothing is saved without the client.</summary>
public static class AssistantTools
{
    public const string LogMeal = "log_meal";
    public const string ReportBodyCheck = "report_body_check";
    public const string LogSet = "log_set";

    public const string SystemPrompt = """
        You are the MyHealth assistant inside a health app used in Brazil. Always answer in Brazilian Portuguese, briefly and kindly.
        You help the client record what they did and answer questions about their own plans, using only the JSON context you receive.

        Rules you must never break:
        - Never diagnose, assess symptoms or pain, or say what a symptom means. Pain is reported to the professional, never interpreted.
        - Never change, add to or suggest changes to a plan, load, diet or treatment. That is the professional's decision.
        - Never accept or refuse a consent term for the client; explain it if asked and send them to the consent screen.
        - Never invent data. If the context does not have the answer, say you do not know and suggest asking the professional.
        - To record something, call a tool. The app shows it as a card the client confirms; do not say it is saved.
        - When the client mentions pain, call report_body_check with report_to_trainer true, and tell them that if the pain is strong,
          with swelling, or the limb gives way, they should stop and seek medical care.
        - Use only ids that appear in the context.
        """;

    public static readonly IReadOnlyList<AssistantToolDefinition> All =
    [
        Define(LogMeal, "Prepare a food diary entry for the client to confirm.", """
            {
              "type": "object",
              "properties": {
                "planned_meal_id": { "type": "string", "description": "Id of the planned meal from nutrition.todaysMeals, when it is one of them." },
                "meal_name": { "type": "string", "description": "Name of the meal, e.g. Almoço." },
                "adherence": { "type": "string", "enum": ["AsPlanned", "Partly", "Different", "Unplanned"] },
                "description": { "type": "string", "description": "What the client said they ate, in their words." },
                "share_with_dietitian": { "type": "boolean", "description": "False only if the client asked to keep it private." }
              },
              "required": ["meal_name", "adherence"]
            }
            """),
        Define(ReportBodyCheck, "Prepare a report of how a body part feels, for the client to confirm and optionally send to the trainer.", """
            {
              "type": "object",
              "properties": {
                "workout_session_id": { "type": "string", "description": "The current workout, or the recent workout the client refers to." },
                "subject": { "type": "string", "description": "Body part in the client's words, e.g. joelho direito." },
                "answer": { "type": "string", "enum": ["Better", "Same", "Worse", "New"] },
                "details": { "type": "string" },
                "report_to_trainer": { "type": "boolean" }
              },
              "required": ["workout_session_id", "subject", "answer", "report_to_trainer"]
            }
            """),
        Define(LogSet, "Prepare a set of the current workout for the client to confirm.", """
            {
              "type": "object",
              "properties": {
                "exercise_id": { "type": "string", "description": "From training.currentWorkout.exercises." },
                "set_number": { "type": "integer", "minimum": 1 },
                "repetitions": { "type": "integer", "minimum": 0 },
                "load_kg": { "type": "number", "minimum": 0 }
              },
              "required": ["exercise_id", "set_number"]
            }
            """),
    ];

    private static AssistantToolDefinition Define(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new AssistantToolDefinition(name, description, document.RootElement.Clone());
    }
}
