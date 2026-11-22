using System.Text.Json.Serialization;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Services;

[JsonConverter(typeof(JsonStringEnumConverter<LessonState>))]
public enum LessonState { Locked, Available, Completed, Mastered }

public record LessonNodeDto(
    int Id, string Slug, string Title, string Summary, string Kind, string Icon, int XpReward,
    LessonState State, bool RequiresPremium, int CompletionCount, double BestScore, IReadOnlyList<string> PrerequisiteSlugs);

public record ChapterDto(
    int Id, string Slug, int Number, string Title, string Subtitle, string Description, bool IsPremium, bool PremiumLocked,
    int TotalLessons, int CompletedLessons, int MasteredLessons, int ProgressPercent, IReadOnlyList<LessonNodeDto> Lessons);

public static class LessonStateCalculator
{
    public static LessonState Compute(Lesson lesson, bool chapterIsPremium, IReadOnlyDictionary<int, UserLessonProgress> progress, bool isPremium)
    {
        if (progress.TryGetValue(lesson.Id, out var p))
            return p.IsMastered ? LessonState.Mastered : LessonState.Completed;

        var prereqsMet = lesson.Prerequisites.All(pr => progress.ContainsKey(pr.PrerequisiteLessonId));
        if (!prereqsMet) return LessonState.Locked;
        return chapterIsPremium && !isPremium ? LessonState.Locked : LessonState.Available;
    }

    public static bool CanPlay(LessonState state, bool chapterIsPremium, bool isPremium) =>
        state != LessonState.Locked && (!chapterIsPremium || isPremium);
}

public class CurriculumService(AppDbContext db, EntitlementService entitlements)
{
    public record UserCurriculum(List<Chapter> Chapters, Dictionary<int, UserLessonProgress> Progress, bool IsPremium)
    {
        public IEnumerable<(Chapter Chapter, Lesson Lesson)> OrderedLessons =>
            Chapters.OrderBy(c => c.SortOrder).SelectMany(c => c.Lessons.OrderBy(l => l.SortOrder).Select(l => (c, l)));

        public LessonState StateOf(Lesson lesson) =>
            LessonStateCalculator.Compute(lesson, Chapters.First(c => c.Id == lesson.ChapterId).IsPremium, Progress, IsPremium);
    }

    public async Task<UserCurriculum> LoadAsync(Guid userId, CancellationToken ct)
    {
        var chapters = await db.Chapters.AsNoTracking()
            .Include(c => c.Lessons).ThenInclude(l => l.Prerequisites)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        var progress = await db.LessonProgress.AsNoTracking().Where(p => p.UserId == userId).ToDictionaryAsync(p => p.LessonId, ct);
        var premium = await entitlements.IsPremiumAsync(userId, ct);
        return new UserCurriculum(chapters, progress, premium);
    }

    public async Task<IReadOnlyList<ChapterDto>> GetTreeAsync(Guid userId, CancellationToken ct) =>
        BuildTree(await LoadAsync(userId, ct));

    public static IReadOnlyList<ChapterDto> BuildTree(UserCurriculum uc)
    {
        var slugById = uc.Chapters.SelectMany(c => c.Lessons).ToDictionary(l => l.Id, l => l.Slug);
        return uc.Chapters.OrderBy(c => c.SortOrder).Select(c =>
        {
            var lessons = c.Lessons.OrderBy(l => l.SortOrder).Select(l =>
            {
                uc.Progress.TryGetValue(l.Id, out var p);
                return new LessonNodeDto(
                    l.Id, l.Slug, l.Title, l.Summary, l.Kind.ToString().ToLowerInvariant(), l.Icon, l.XpReward,
                    uc.StateOf(l), c.IsPremium && !uc.IsPremium, p?.CompletionCount ?? 0, p?.BestScore ?? 0,
                    l.Prerequisites.Select(pr => slugById[pr.PrerequisiteLessonId]).ToList());
            }).ToList();

            var completed = lessons.Count(l => l.State is LessonState.Completed or LessonState.Mastered);
            var mastered = lessons.Count(l => l.State == LessonState.Mastered);
            var percent = lessons.Count == 0 ? 0 : (int)Math.Round(100.0 * completed / lessons.Count);
            return new ChapterDto(c.Id, c.Slug, c.Number, c.Title, c.Subtitle, c.Description, c.IsPremium, c.IsPremium && !uc.IsPremium,
                lessons.Count, completed, mastered, percent, lessons);
        }).ToList();
    }

    public static LessonNodeDto? NextRecommended(IReadOnlyList<ChapterDto> tree)
    {
        var all = tree.SelectMany(c => c.Lessons).ToList();
        return all.FirstOrDefault(l => l.State == LessonState.Available)
               ?? all.FirstOrDefault(l => l.State == LessonState.Completed && !l.RequiresPremium);
    }
}

