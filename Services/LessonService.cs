using System.Text.Json;
using System.Text.Json.Nodes;
using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Services;

public record ExerciseDto(int Id, string Key, string Type, bool Graded, JsonNode Content);

public record LessonPlayDto(
    int Id, string Slug, string Title, string Summary, string Kind, int XpReward, LessonState State,
    string ChapterSlug, string ChapterTitle, int CompletionCount, IReadOnlyList<ExerciseDto> Exercises);

public record CheckAnswerResult(bool IsCorrect, string? Explanation, JsonNode? CorrectAnswer);

public record CompletedLessonDto(int Id, string Slug, string Title);

public record CompleteLessonResult(
    int XpEarned, double Score, int CorrectCount, int GradedCount, bool FirstCompletion, bool IsMastered, bool NewlyMastered,
    int ReviewItemsScheduled, int CurrentStreak, bool StreakExtended, IReadOnlyList<CompletedLessonDto> UnlockedLessons, CompletedLessonDto? NextLesson);

public class LessonService(AppDbContext db, CurriculumService curriculum, TimeProvider clock)
{
    public async Task<LessonPlayDto> GetForPlayAsync(Guid userId, string slug, CancellationToken ct)
    {
        var uc = await curriculum.LoadAsync(userId, ct);
        var (chapter, lesson, state) = RequirePlayable(uc, l => l.Slug == slug);

        var exercises = await db.Exercises.AsNoTracking().Where(e => e.LessonId == lesson.Id).OrderBy(e => e.SortOrder).ToListAsync(ct);
        uc.Progress.TryGetValue(lesson.Id, out var p);

        return new LessonPlayDto(lesson.Id, lesson.Slug, lesson.Title, lesson.Summary, lesson.Kind.ToString().ToLowerInvariant(), lesson.XpReward, state,
            chapter.Slug, chapter.Title, p?.CompletionCount ?? 0,
            exercises.Select(e => new ExerciseDto(e.Id, e.Key, e.Type, e.IsGraded, JsonNode.Parse(e.ContentJson)!)).ToList());
    }

    public async Task<CheckAnswerResult> CheckAnswerAsync(Guid userId, int lessonId, int exerciseId, JsonElement answer, CancellationToken ct)
    {
        var uc = await curriculum.LoadAsync(userId, ct);
        RequirePlayable(uc, l => l.Id == lessonId);

        var exercise = await db.Exercises.FirstOrDefaultAsync(e => e.Id == exerciseId && e.LessonId == lessonId, ct)
                       ?? throw AppException.NotFound("Exercise");
        if (!exercise.IsGraded) throw AppException.Validation("This step has no answer to check.");

        var grade = ExerciseGrader.Grade(exercise.Type, exercise.AnswerJson!, answer);
        db.ExerciseAttempts.Add(new UserExerciseAttempt
        {
            UserId = userId,
            ExerciseId = exercise.Id,
            AnswerJson = answer.GetRawText(),
            IsCorrect = grade.IsCorrect,
            AttemptedAt = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);

        return new CheckAnswerResult(grade.IsCorrect, grade.Explanation, grade.CorrectAnswer);
    }

    public async Task<CompleteLessonResult> CompleteAsync(Guid userId, int lessonId, IReadOnlyDictionary<int, JsonElement> firstAnswers, CancellationToken ct)
    {
        var uc = await curriculum.LoadAsync(userId, ct);
        var (_, lesson, _) = RequirePlayable(uc, l => l.Id == lessonId);

        var graded = await db.Exercises.AsNoTracking().Where(e => e.LessonId == lessonId && e.AnswerJson != null).OrderBy(e => e.SortOrder).ToListAsync(ct);
        var missing = graded.Where(e => !firstAnswers.ContainsKey(e.Id)).ToList();
        if (missing.Count > 0) throw AppException.Validation("Every question must be answered before the lesson can be completed.");

        var correct = graded.Count(e => ExerciseGrader.Grade(e.Type, e.AnswerJson!, firstAnswers[e.Id]).IsCorrect);
        var score = graded.Count == 0 ? 1.0 : (double)correct / graded.Count;

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.Include(u => u.Streak).FirstAsync(u => u.Id == userId, ct);
        var today = UserClock.LocalDate(now, user.TimeZoneId);

        var progress = await db.LessonProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.LessonId == lessonId, ct);
        var firstCompletion = progress is null;
        var wasMastered = progress?.IsMastered ?? false;
        if (progress is null)
        {
            progress = new UserLessonProgress { UserId = userId, LessonId = lessonId, FirstCompletedAt = now };
            db.LessonProgress.Add(progress);
        }
        progress.LastCompletedAt = now;
        progress.CompletionCount++;
        progress.BestScore = Math.Max(progress.BestScore, score);
        progress.IsMastered = wasMastered || score >= XpRules.MasteryScore;
        var newlyMastered = progress.IsMastered && !wasMastered;

        var xp = 0;
        void Award(int amount, XpSource source)
        {
            xp += amount;
            db.XpTransactions.Add(new XpTransaction { UserId = userId, Amount = amount, Source = source, Reference = $"lesson:{lesson.Slug}", CreatedAt = now });
        }
        if (firstCompletion) Award(lesson.XpReward, XpSource.LessonCompleted);
        else Award(XpRules.ReplayXp(lesson.XpReward), XpSource.LessonReplayed);
        if (newlyMastered) Award(XpRules.PerfectBonus, XpSource.PerfectBonus);

        var scheduled = 0;
        var itemIds = await db.ReviewItems.Where(r => r.LessonId == lessonId).Select(r => r.Id).ToListAsync(ct);
        if (itemIds.Count > 0)
        {
            var existing = await db.ReviewStates.Where(s => s.UserId == userId && itemIds.Contains(s.ReviewItemId)).Select(s => s.ReviewItemId).ToListAsync(ct);
            var firstDue = UserClock.StartOfLocalDayUtc(today.AddDays(1), user.TimeZoneId);
            foreach (var id in itemIds.Except(existing))
            {
                db.ReviewStates.Add(new UserReviewState { UserId = userId, ReviewItemId = id, DueAt = firstDue, CreatedAt = now });
                scheduled++;
            }
        }

        user.Streak ??= new UserStreak { UserId = userId };
        var streakExtended = StreakRules.RegisterActivity(user.Streak, today);

        await db.SaveChangesAsync(ct);

        var before = uc.OrderedLessons.ToDictionary(x => x.Lesson.Id, x => uc.StateOf(x.Lesson));
        uc.Progress[lessonId] = progress;
        var unlocked = uc.OrderedLessons
            .Where(x => before[x.Lesson.Id] == LessonState.Locked && uc.StateOf(x.Lesson) == LessonState.Available)
            .Select(x => new CompletedLessonDto(x.Lesson.Id, x.Lesson.Slug, x.Lesson.Title))
            .ToList();
        var next = uc.OrderedLessons.FirstOrDefault(x => uc.StateOf(x.Lesson) == LessonState.Available).Lesson;

        return new CompleteLessonResult(xp, Math.Round(score, 3), correct, graded.Count, firstCompletion, progress.IsMastered, newlyMastered,
            scheduled, user.Streak.CurrentStreak, streakExtended, unlocked,
            next is null ? null : new CompletedLessonDto(next.Id, next.Slug, next.Title));
    }

    private static (Chapter Chapter, Lesson Lesson, LessonState State) RequirePlayable(CurriculumService.UserCurriculum uc, Func<Lesson, bool> match)
    {
        var (chapter, lesson) = uc.OrderedLessons.FirstOrDefault(x => match(x.Lesson));
        if (lesson is null) throw AppException.NotFound("Lesson");

        if (chapter.IsPremium && !uc.IsPremium)
            throw AppException.Forbidden("premium_required", "This lesson is part of Premium. Upgrade to unlock it.");

        var state = uc.StateOf(lesson);
        if (!LessonStateCalculator.CanPlay(state, chapter.IsPremium, uc.IsPremium))
            throw AppException.Forbidden("lesson_locked", "Finish the earlier lessons to unlock this one.");

        return (chapter, lesson, state);
    }
}

