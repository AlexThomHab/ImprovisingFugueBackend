using System.Text.Json;
using Improvising_Fugue.Common;
using Improvising_Fugue.Models;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

public class LessonsController(LessonService lessons) : ApiControllerBase
{
    [HttpGet("{slug}")]
    public Task<LessonPlayDto> Get(string slug, CancellationToken ct) => lessons.GetForPlayAsync(UserId, slug, ct);

    [HttpPost("{lessonId:int}/exercises/{exerciseId:int}/check")]
    public Task<CheckAnswerResult> Check(int lessonId, int exerciseId, CheckAnswerRequest req, CancellationToken ct) =>
        lessons.CheckAnswerAsync(UserId, lessonId, exerciseId, req.Answer, ct);

    [HttpPost("{lessonId:int}/complete")]
    public Task<CompleteLessonResult> Complete(int lessonId, CompleteLessonRequest req, CancellationToken ct)
    {
        if (req.Answers is null) throw AppException.Validation("Answers are required.");
        var answers = new Dictionary<int, JsonElement>();
        foreach (var a in req.Answers) answers.TryAdd(a.ExerciseId, a.Answer);
        return lessons.CompleteAsync(UserId, lessonId, answers, ct);
    }
}
