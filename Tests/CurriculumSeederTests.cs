using Improvising_Fugue.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImprovisingFugue.Tests;

public class CurriculumSeederTests : IDisposable
{
    private readonly TestHarness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public async Task Reseeding_is_idempotent_and_keeps_progress()
    {
        var user = await h.CreateUserAsync();
        await h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, h.Answers("l1"), default);
        var counts = (await h.Db.Lessons.CountAsync(), await h.Db.Exercises.CountAsync(), await h.Db.ReviewItems.CountAsync());

        using var ctx = h.NewContext();
        await new CurriculumSeeder(ctx, NullLogger<CurriculumSeeder>.Instance).SeedAsync(TestHarness.SampleCurriculum());

        Assert.Equal(counts, (await ctx.Lessons.CountAsync(), await ctx.Exercises.CountAsync(), await ctx.ReviewItems.CountAsync()));
        Assert.Equal(1, await ctx.LessonProgress.CountAsync());
    }

    [Fact]
    public async Task Default_prerequisite_is_the_previous_lesson_across_chapters()
    {
        var prereqs = await h.Db.LessonPrerequisites.Include(p => p.Lesson).Include(p => p.PrerequisiteLesson)
            .ToDictionaryAsync(p => p.Lesson!.Slug, p => p.PrerequisiteLesson!.Slug);
        Assert.Equal("l1", prereqs["l2"]);
        Assert.Equal("l2", prereqs["p1"]);
        Assert.False(prereqs.ContainsKey("l1"));
    }

    [Fact]
    public async Task Shipped_curriculum_files_are_valid()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Content", "Curriculum");
        Assert.True(Directory.Exists(dir), $"Missing {dir}");

        using var ctx = h.NewContext();
        await ctx.Database.EnsureDeletedAsync();
        await ctx.Database.EnsureCreatedAsync();
        await new CurriculumSeeder(ctx, NullLogger<CurriculumSeeder>.Instance).SeedFromDirectoryAsync(dir);

        var ch1 = await ctx.Chapters.Include(c => c.Lessons).SingleAsync(c => c.Number == 1);
        Assert.False(ch1.IsPremium);
        Assert.Equal(7, ch1.Lessons.Count);
        Assert.True(await ctx.Chapters.Where(c => c.Number > 1).AllAsync(c => c.IsPremium));
    }
}
