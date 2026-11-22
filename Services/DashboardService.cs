using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Services;

public record DailyXpDto(DateOnly Date, int Xp);
public record ChapterProgressDto(string Slug, int Number, string Title, int CompletedLessons, int TotalLessons, int ProgressPercent, bool PremiumLocked);

public record DashboardDto(
    string DisplayName, bool IsPremium,
    int TotalXp, int XpToday, int DailyGoalXp,
    int CurrentStreak, int LongestStreak, bool ActiveToday,
    int LessonsCompleted, int LessonsMastered, int TotalLessons,
    LessonNodeDto? NextLesson, ReviewSummaryDto Reviews,
    IReadOnlyList<ChapterProgressDto> Chapters, IReadOnlyList<DailyXpDto> LastSevenDays);

public class DashboardService(AppDbContext db, CurriculumService curriculum, ReviewService reviews, TimeProvider clock)
{
    public async Task<DashboardDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Streak).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw AppException.NotFound("User");

        var now = clock.GetUtcNow().UtcDateTime;
        var today = UserClock.LocalDate(now, user.TimeZoneId);
        var weekStart = UserClock.StartOfLocalDayUtc(today.AddDays(-6), user.TimeZoneId);

        var totalXp = await db.XpTransactions.Where(x => x.UserId == userId).SumAsync(x => (int?)x.Amount, ct) ?? 0;
        var recent = await db.XpTransactions.Where(x => x.UserId == userId && x.CreatedAt >= weekStart)
            .Select(x => new { x.CreatedAt, x.Amount }).ToListAsync(ct);
        var byDay = recent.GroupBy(x => UserClock.LocalDate(x.CreatedAt, user.TimeZoneId)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var lastSeven = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(d => new DailyXpDto(d, byDay.GetValueOrDefault(d))).ToList();

        var uc = await curriculum.LoadAsync(userId, ct);
        var tree = CurriculumService.BuildTree(uc);
        var lessons = tree.SelectMany(c => c.Lessons).ToList();

        return new DashboardDto(
            user.DisplayName, uc.IsPremium,
            totalXp, byDay.GetValueOrDefault(today), user.DailyGoalXp,
            StreakRules.Displayed(user.Streak, today), user.Streak?.LongestStreak ?? 0, user.Streak?.LastActivityDate == today,
            lessons.Count(l => l.State is LessonState.Completed or LessonState.Mastered),
            lessons.Count(l => l.State == LessonState.Mastered),
            lessons.Count,
            CurriculumService.NextRecommended(tree),
            await reviews.GetSummaryAsync(userId, ct),
            tree.Select(c => new ChapterProgressDto(c.Slug, c.Number, c.Title, c.CompletedLessons, c.TotalLessons, c.ProgressPercent, c.PremiumLocked)).ToList(),
            lastSeven);
    }
}
