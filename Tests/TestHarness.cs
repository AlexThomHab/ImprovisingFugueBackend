using System.Text.Json;
using System.Text.Json.Nodes;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Improvising_Fugue.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Improvising_Fugue.Data.CurriculumSeeder;

namespace ImprovisingFugue.Tests;

public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class TestHarness : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
    public AppDbContext Db { get; }

    public TestHarness()
    {
        _conn.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();
        new CurriculumSeeder(Db, NullLogger<CurriculumSeeder>.Instance).SeedAsync(SampleCurriculum()).GetAwaiter().GetResult();
    }

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);

    public EntitlementService Entitlements => new(Db, Clock);
    public CurriculumService Curriculum => new(Db, Entitlements);
    public LessonService Lessons => new(Db, Curriculum, Clock);
    public ReviewService Reviews => new(Db, new Sm2Scheduler(), Clock);

    public async Task<User> CreateUserAsync(bool premium = false)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@test.dev",
            NormalizedEmail = Guid.NewGuid().ToString("N"),
            PasswordHash = "x",
            DisplayName = "Tester",
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
            Streak = new UserStreak(),
            Subscription = new Subscription { Status = premium ? SubscriptionStatus.Active : SubscriptionStatus.None },
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    public Lesson Lesson(string slug) => Db.Lessons.Include(l => l.Exercises).Single(l => l.Slug == slug);

    public Dictionary<int, JsonElement> Answers(string slug, params string[] wrongKeys) =>
        Lesson(slug).Exercises.Where(e => e.IsGraded).ToDictionary(e => e.Id, e =>
            JsonSerializer.SerializeToElement(new { selectedIndex = wrongKeys.Contains(e.Key) ? 0 : 1 }));

    public static List<ChapterDoc> SampleCurriculum()
    {
        static ExerciseDoc Mc(string key) => new(key, ExerciseTypes.MultipleChoice,
            new JsonObject { ["prompt"] = "?", ["options"] = new JsonArray("a", "b") },
            new JsonObject { ["correctIndex"] = 1, ["explanation"] = "b is right" });
        static ExerciseDoc Info(string key) => new(key, ExerciseTypes.Info, new JsonObject { ["title"] = "t" }, null);

        return
        [
            new ChapterDoc("ch1", 1, "Chapter 1", null, null, false, null,
            [
                new LessonDoc("l1", "Lesson 1", null, null, 10, null, null, [Info("i"), Mc("q1"), Mc("q2")],
                    [new ReviewItemDoc("r1", "front 1", "back 1", null, null), new ReviewItemDoc("r2", "front 2", "back 2", null, null)]),
                new LessonDoc("l2", "Lesson 2", null, null, 10, null, null, [Mc("q1")], null),
            ]),
            new ChapterDoc("ch2", 2, "Chapter 2", null, null, true, null,
            [
                new LessonDoc("p1", "Premium 1", null, null, 10, null, null, [Mc("q1")], null),
            ]),
        ];
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
