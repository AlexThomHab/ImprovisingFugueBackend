using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Services;

public record ReviewCardDto(Guid StateId, int ReviewItemId, string Front, string Back, string? Hint, JsonNode? Notation,
    string LessonSlug, string LessonTitle, int Repetitions, double IntervalDays, DateTime DueAt);

public record ReviewSummaryDto(int DueNow, int DueToday, int TotalLearned, int ReviewedToday, IReadOnlyList<UpcomingDayDto> Upcoming);
public record UpcomingDayDto(DateOnly Date, int Count);

public record RateResultDto(Guid StateId, DateTime NextDueAt, double IntervalDays, int XpEarned, int CurrentStreak, bool StreakExtended);

public class ReviewService(AppDbContext db, IReviewScheduler scheduler, TimeProvider clock)
{
    public async Task<IReadOnlyList<ReviewCardDto>> GetDueAsync(Guid userId, int limit, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var states = await StatesFor(userId).Where(s => s.DueAt <= now).OrderBy(s => s.DueAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        return states.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ReviewCardDto>> GetPracticeAsync(Guid userId, int count, CancellationToken ct)
    {
        var states = await StatesFor(userId).ToListAsync(ct);
        return states
            .OrderBy(s => s.EaseFactor)
            .ThenBy(_ => Random.Shared.Next())
            .Take(Math.Clamp(count, 1, 50))
            .OrderBy(_ => Random.Shared.Next())
            .Select(ToDto).ToList();
    }

    public async Task<RateResultDto> RateAsync(Guid userId, Guid stateId, ReviewRating rating, bool practice, CancellationToken ct)
    {
        if (!Enum.IsDefined(rating)) throw AppException.Validation("Rating must be Again, Hard, Good or Easy.");

        var state = await db.ReviewStates.FirstOrDefaultAsync(s => s.Id == stateId && s.UserId == userId, ct)
                    ?? throw AppException.NotFound("Review item");
        var now = clock.GetUtcNow().UtcDateTime;
        var prevInterval = state.IntervalDays;

        if (!practice)
        {
            var next = scheduler.Next(state, rating, now);
            state.EaseFactor = next.EaseFactor;
            state.IntervalDays = next.IntervalDays;
            state.Repetitions = next.Repetitions;
            state.Lapses = next.Lapses;
            state.DueAt = next.DueAt;
            state.LastReviewedAt = now;
        }

        db.ReviewLogs.Add(new ReviewLog
        {
            UserReviewStateId = state.Id,
            UserId = userId,
            Rating = rating,
            ReviewedAt = now,
            PreviousIntervalDays = prevInterval,
            NewIntervalDays = state.IntervalDays,
            IsPractice = practice,
        });
        db.XpTransactions.Add(new XpTransaction { UserId = userId, Amount = XpRules.PerReview, Source = XpSource.Review, Reference = $"review:{state.ReviewItemId}", CreatedAt = now });

        var user = await db.Users.Include(u => u.Streak).FirstAsync(u => u.Id == userId, ct);
        user.Streak ??= new UserStreak { UserId = userId };
        var extended = StreakRules.RegisterActivity(user.Streak, UserClock.LocalDate(now, user.TimeZoneId));

        await db.SaveChangesAsync(ct);
        return new RateResultDto(state.Id, state.DueAt, state.IntervalDays, XpRules.PerReview, user.Streak.CurrentStreak, extended);
    }

    public async Task<ReviewSummaryDto> GetSummaryAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tz = await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstAsync(ct);
        var today = UserClock.LocalDate(now, tz);
        var startToday = UserClock.StartOfLocalDayUtc(today, tz);
        var endToday = UserClock.StartOfLocalDayUtc(today.AddDays(1), tz);
        var horizon = UserClock.StartOfLocalDayUtc(today.AddDays(8), tz);

        var dues = await db.ReviewStates.Where(s => s.UserId == userId).Select(s => s.DueAt).ToListAsync(ct);
        var reviewedToday = await db.ReviewLogs.CountAsync(l => l.UserId == userId && l.ReviewedAt >= startToday, ct);

        var upcoming = Enumerable.Range(1, 7).Select(offset =>
        {
            var day = today.AddDays(offset);
            var from = UserClock.StartOfLocalDayUtc(day, tz);
            var to = UserClock.StartOfLocalDayUtc(day.AddDays(1), tz);
            return new UpcomingDayDto(day, dues.Count(d => d >= from && d < to && d < horizon));
        }).ToList();

        return new ReviewSummaryDto(dues.Count(d => d <= now), dues.Count(d => d < endToday), dues.Count, reviewedToday, upcoming);
    }

    private IQueryable<UserReviewState> StatesFor(Guid userId) =>
        db.ReviewStates.AsNoTracking().Where(s => s.UserId == userId)
            .Include(s => s.ReviewItem).ThenInclude(r => r!.Lesson);

    private static ReviewCardDto ToDto(UserReviewState s) => new(
        s.Id, s.ReviewItemId, s.ReviewItem!.Front, s.ReviewItem.Back, s.ReviewItem.Hint,
        s.ReviewItem.NotationJson is null ? null : JsonNode.Parse(s.ReviewItem.NotationJson),
        s.ReviewItem.Lesson!.Slug, s.ReviewItem.Lesson.Title, s.Repetitions, s.IntervalDays, s.DueAt);
}

