using Improvising_Fugue.Common;
using Improvising_Fugue.Domain;
using Improvising_Fugue.Services;
using Microsoft.EntityFrameworkCore;

namespace ImprovisingFugue.Tests;

public class ReviewSchedulerTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly Sm2Scheduler s = new();

    private static UserReviewState Apply(UserReviewState st, ReviewSchedule r)
    {
        st.EaseFactor = r.EaseFactor; st.IntervalDays = r.IntervalDays; st.Repetitions = r.Repetitions; st.Lapses = r.Lapses; st.DueAt = r.DueAt;
        return st;
    }

    [Fact]
    public void Good_answers_grow_the_interval_1_3_then_by_ease()
    {
        var st = new UserReviewState();
        Assert.Equal(1, Apply(st, s.Next(st, ReviewRating.Good, Now)).IntervalDays);
        Assert.Equal(3, Apply(st, s.Next(st, ReviewRating.Good, Now)).IntervalDays);
        Assert.Equal(8, Apply(st, s.Next(st, ReviewRating.Good, Now)).IntervalDays);
        Assert.Equal(Now.AddDays(8), st.DueAt);
    }

    [Fact]
    public void Again_resets_repetitions_lowers_ease_and_requeues_shortly()
    {
        var st = new UserReviewState { Repetitions = 4, IntervalDays = 20, EaseFactor = 2.5 };
        var r = s.Next(st, ReviewRating.Again, Now);
        Assert.Equal(0, r.Repetitions);
        Assert.Equal(1, r.Lapses);
        Assert.Equal(2.3, r.EaseFactor, 3);
        Assert.Equal(Now + Sm2Scheduler.RelearnDelay, r.DueAt);
    }

    [Fact]
    public void Easy_beats_good_beats_hard()
    {
        var st = new UserReviewState { Repetitions = 3, IntervalDays = 10, EaseFactor = 2.5 };
        var hard = s.Next(st, ReviewRating.Hard, Now).IntervalDays;
        var good = s.Next(st, ReviewRating.Good, Now).IntervalDays;
        var easy = s.Next(st, ReviewRating.Easy, Now).IntervalDays;
        Assert.True(hard < good && good < easy, $"{hard} < {good} < {easy}");
    }

    [Fact]
    public void Ease_never_drops_below_floor_and_interval_is_capped()
    {
        var st = new UserReviewState { EaseFactor = 1.3 };
        Assert.Equal(Sm2Scheduler.MinEase, s.Next(st, ReviewRating.Again, Now).EaseFactor);

        var mature = new UserReviewState { Repetitions = 10, IntervalDays = 300, EaseFactor = 3 };
        Assert.Equal(Sm2Scheduler.MaxIntervalDays, s.Next(mature, ReviewRating.Easy, Now).IntervalDays);
    }
}

public class ReviewServiceTests : IDisposable
{
    private readonly TestHarness h = new();
    public void Dispose() => h.Dispose();

    private async Task<User> UserWithReviewsAsync()
    {
        var user = await h.CreateUserAsync();
        await h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, h.Answers("l1"), default);
        return user;
    }

    [Fact]
    public async Task Items_become_due_the_next_day()
    {
        var user = await UserWithReviewsAsync();
        Assert.Empty(await h.Reviews.GetDueAsync(user.Id, 20, default));

        var summary = await h.Reviews.GetSummaryAsync(user.Id, default);
        Assert.Equal(2, summary.TotalLearned);
        Assert.Equal(2, summary.Upcoming[0].Count);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, (await h.Reviews.GetDueAsync(user.Id, 20, default)).Count);
    }

    [Fact]
    public async Task Rating_reschedules_logs_history_and_awards_xp()
    {
        var user = await UserWithReviewsAsync();
        h.Clock.Advance(TimeSpan.FromDays(1));
        var card = (await h.Reviews.GetDueAsync(user.Id, 20, default))[0];

        var result = await h.Reviews.RateAsync(user.Id, card.StateId, ReviewRating.Good, practice: false, default);

        Assert.Equal(1, result.IntervalDays);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime.AddDays(1), result.NextDueAt);
        Assert.Single(await h.Reviews.GetDueAsync(user.Id, 20, default));
        Assert.Equal(1, await h.Db.ReviewLogs.CountAsync(l => l.UserId == user.Id && !l.IsPractice));
        Assert.Equal(2, result.CurrentStreak);
    }

    [Fact]
    public async Task Practice_does_not_change_the_schedule()
    {
        var user = await UserWithReviewsAsync();
        var card = (await h.Reviews.GetPracticeAsync(user.Id, 10, default))[0];
        var before = await h.Db.ReviewStates.AsNoTracking().SingleAsync(s => s.Id == card.StateId);

        await h.Reviews.RateAsync(user.Id, card.StateId, ReviewRating.Easy, practice: true, default);

        using var fresh = h.NewContext();
        var after = await fresh.ReviewStates.SingleAsync(s => s.Id == card.StateId);
        Assert.Equal(before.DueAt, after.DueAt);
        Assert.Equal(before.Repetitions, after.Repetitions);
        Assert.Equal(1, await fresh.ReviewLogs.CountAsync(l => l.IsPractice));
    }

    [Fact]
    public async Task Users_cannot_rate_someone_elses_review_item()
    {
        var owner = await UserWithReviewsAsync();
        var intruder = await h.CreateUserAsync();
        var card = (await h.Reviews.GetPracticeAsync(owner.Id, 1, default))[0];

        var ex = await Assert.ThrowsAsync<AppException>(() => h.Reviews.RateAsync(intruder.Id, card.StateId, ReviewRating.Good, false, default));
        Assert.Equal(404, ex.StatusCode);
    }
}
