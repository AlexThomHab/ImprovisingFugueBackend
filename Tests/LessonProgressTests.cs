using Improvising_Fugue.Common;
using Improvising_Fugue.Domain;
using Improvising_Fugue.Services;
using Microsoft.EntityFrameworkCore;

namespace ImprovisingFugue.Tests;

public class LessonProgressTests : IDisposable
{
    private readonly TestHarness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public async Task New_user_sees_only_first_lesson_available()
    {
        var user = await h.CreateUserAsync();
        var tree = await h.Curriculum.GetTreeAsync(user.Id, default);

        Assert.Equal(LessonState.Available, tree[0].Lessons[0].State);
        Assert.Equal(LessonState.Locked, tree[0].Lessons[1].State);
        Assert.True(tree[1].PremiumLocked);
        Assert.All(tree[1].Lessons, l => Assert.True(l.RequiresPremium));
    }

    [Fact]
    public async Task Completing_a_lesson_awards_xp_unlocks_next_and_schedules_reviews()
    {
        var user = await h.CreateUserAsync();
        var result = await h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, h.Answers("l1"), default);

        Assert.True(result.FirstCompletion);
        Assert.Equal(10 + XpRules.PerfectBonus, result.XpEarned);
        Assert.True(result.NewlyMastered);
        Assert.Equal("l2", Assert.Single(result.UnlockedLessons).Slug);
        Assert.Equal(2, result.ReviewItemsScheduled);
        Assert.Equal(1, result.CurrentStreak);

        var states = await h.Db.ReviewStates.Where(s => s.UserId == user.Id).ToListAsync();
        Assert.All(states, s => Assert.Equal(new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc), s.DueAt));

        var tree = await h.Curriculum.GetTreeAsync(user.Id, default);
        Assert.Equal(LessonState.Mastered, tree[0].Lessons[0].State);
        Assert.Equal(LessonState.Available, tree[0].Lessons[1].State);
        Assert.Equal(50, tree[0].ProgressPercent);
    }

    [Fact]
    public async Task Score_uses_first_answers_and_imperfect_score_is_completed_not_mastered()
    {
        var user = await h.CreateUserAsync();
        var result = await h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, h.Answers("l1", "q2"), default);

        Assert.Equal(0.5, result.Score);
        Assert.False(result.IsMastered);
        Assert.Equal(10, result.XpEarned);
    }

    [Fact]
    public async Task Replaying_awards_reduced_xp_and_does_not_duplicate_reviews()
    {
        var user = await h.CreateUserAsync();
        var id = h.Lesson("l1").Id;
        await h.Lessons.CompleteAsync(user.Id, id, h.Answers("l1", "q1"), default);
        var replay = await h.Lessons.CompleteAsync(user.Id, id, h.Answers("l1"), default);

        Assert.False(replay.FirstCompletion);
        Assert.Equal(XpRules.ReplayXp(10) + XpRules.PerfectBonus, replay.XpEarned);
        Assert.True(replay.NewlyMastered);
        Assert.Equal(0, replay.ReviewItemsScheduled);
        Assert.Equal(2, await h.Db.ReviewStates.CountAsync(s => s.UserId == user.Id));

        var progress = await h.Db.LessonProgress.SingleAsync(p => p.UserId == user.Id);
        Assert.Equal(2, progress.CompletionCount);
        Assert.Equal(1.0, progress.BestScore);
    }

    [Fact]
    public async Task Completing_requires_an_answer_for_every_graded_exercise()
    {
        var user = await h.CreateUserAsync();
        var answers = h.Answers("l1");
        answers.Remove(answers.Keys.First());

        var ex = await Assert.ThrowsAsync<AppException>(() => h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, answers, default));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Locked_lesson_cannot_be_played_or_completed()
    {
        var user = await h.CreateUserAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() => h.Lessons.GetForPlayAsync(user.Id, "l2", default));
        Assert.Equal("lesson_locked", ex.Code);
        await Assert.ThrowsAsync<AppException>(() => h.Lessons.CompleteAsync(user.Id, h.Lesson("l2").Id, h.Answers("l2"), default));
    }

    [Fact]
    public async Task Premium_chapter_requires_premium_even_after_prerequisites()
    {
        var free = await h.CreateUserAsync();
        await h.Lessons.CompleteAsync(free.Id, h.Lesson("l1").Id, h.Answers("l1"), default);
        await h.Lessons.CompleteAsync(free.Id, h.Lesson("l2").Id, h.Answers("l2"), default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.Lessons.GetForPlayAsync(free.Id, "p1", default));
        Assert.Equal("premium_required", ex.Code);
        Assert.Equal(403, ex.StatusCode);

        var premium = await h.CreateUserAsync(premium: true);
        await h.Lessons.CompleteAsync(premium.Id, h.Lesson("l1").Id, h.Answers("l1"), default);
        await h.Lessons.CompleteAsync(premium.Id, h.Lesson("l2").Id, h.Answers("l2"), default);
        var play = await h.Lessons.GetForPlayAsync(premium.Id, "p1", default);
        Assert.Equal(LessonState.Available, play.State);
    }

    [Fact]
    public async Task Check_answer_records_attempt_and_hides_nothing_after_grading()
    {
        var user = await h.CreateUserAsync();
        var lesson = h.Lesson("l1");
        var q = lesson.Exercises.First(e => e.Key == "q1");

        var result = await h.Lessons.CheckAnswerAsync(user.Id, lesson.Id, q.Id, h.Answers("l1", "q1")[q.Id], default);

        Assert.False(result.IsCorrect);
        Assert.Equal("b is right", result.Explanation);
        Assert.Equal(1, await h.Db.ExerciseAttempts.CountAsync(a => a.UserId == user.Id && !a.IsCorrect));
    }

    [Fact]
    public async Task Lesson_content_sent_to_client_never_contains_answers()
    {
        var user = await h.CreateUserAsync();
        var play = await h.Lessons.GetForPlayAsync(user.Id, "l1", default);
        Assert.All(play.Exercises, e => Assert.Null(e.Content["correctIndex"]));
    }

    [Fact]
    public async Task Progress_is_isolated_per_user()
    {
        var a = await h.CreateUserAsync();
        var b = await h.CreateUserAsync();
        await h.Lessons.CompleteAsync(a.Id, h.Lesson("l1").Id, h.Answers("l1"), default);

        var treeB = await h.Curriculum.GetTreeAsync(b.Id, default);
        Assert.Equal(LessonState.Available, treeB[0].Lessons[0].State);
        Assert.Equal(LessonState.Locked, treeB[0].Lessons[1].State);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(-1, 4)]
    [InlineData(-2, 1)]
    public void Streak_rules(int? lastOffset, int expected)
    {
        var today = new DateOnly(2026, 3, 10);
        var streak = new UserStreak { CurrentStreak = 3, LongestStreak = 3, LastActivityDate = lastOffset is null ? null : today.AddDays(lastOffset.Value) };

        Assert.True(StreakRules.RegisterActivity(streak, today));
        Assert.Equal(expected, streak.CurrentStreak);
        Assert.False(StreakRules.RegisterActivity(streak, today));
        Assert.Equal(expected, streak.CurrentStreak);
    }

    [Fact]
    public void Displayed_streak_drops_to_zero_after_a_missed_day()
    {
        var s = new UserStreak { CurrentStreak = 5, LastActivityDate = new DateOnly(2026, 3, 8) };
        Assert.Equal(5, StreakRules.Displayed(s, new DateOnly(2026, 3, 9)));
        Assert.Equal(0, StreakRules.Displayed(s, new DateOnly(2026, 3, 10)));
    }
}
