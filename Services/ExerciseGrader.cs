using System.Text.Json;
using System.Text.Json.Nodes;
using Improvising_Fugue.Common;
using Improvising_Fugue.Data;

namespace Improvising_Fugue.Services;

public record GradeResult(bool IsCorrect, string? Explanation, JsonNode? CorrectAnswer);

public static class ExerciseGrader
{
    public static GradeResult Grade(string type, string answerKeyJson, JsonElement submitted)
    {
        var key = JsonNode.Parse(answerKeyJson)!.AsObject();
        var explanation = key["explanation"]?.GetValue<string>();

        try
        {
            switch (type)
            {
                case ExerciseTypes.MultipleChoice:
                {
                    var correct = key["correctIndex"]!.GetValue<int>();
                    var selected = submitted.GetProperty("selectedIndex").GetInt32();
                    return new(selected == correct, explanation, new JsonObject { ["correctIndex"] = correct });
                }
                case ExerciseTypes.Ordering:
                {
                    var correct = key["order"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                    var given = submitted.GetProperty("order").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    return new(correct.SequenceEqual(given), explanation, new JsonObject { ["order"] = new JsonArray(correct.Select(c => (JsonNode)c!).ToArray()) });
                }
                case ExerciseTypes.Keyboard:
                {
                    var correct = key["pitchClasses"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
                    var given = submitted.GetProperty("midi").EnumerateArray().Select(e => ((e.GetInt32() % 12) + 12) % 12).ToHashSet();
                    return new(correct.SetEquals(given), explanation, new JsonObject { ["pitchClasses"] = new JsonArray(correct.Select(c => (JsonNode)c).ToArray()) });
                }
                default:
                    throw AppException.Validation($"Exercise type '{type}' cannot be graded.");
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw AppException.Validation("Answer payload is not valid for this exercise.");
        }
    }
}
