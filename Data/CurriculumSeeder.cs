using System.Text.Json;
using System.Text.Json.Nodes;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Data;

public class CurriculumSeeder(AppDbContext db, ILogger<CurriculumSeeder> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SeedFromDirectoryAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("Curriculum directory {Dir} not found; skipping seed", directory);
            return;
        }

        var files = Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var docs = new List<ChapterDoc>();
        foreach (var file in files)
        {
            var doc = JsonSerializer.Deserialize<ChapterDoc>(await File.ReadAllTextAsync(file, ct), Json)
                      ?? throw new InvalidOperationException($"Empty curriculum file {file}");
            docs.Add(doc);
        }

        await SeedAsync(docs, ct);
        logger.LogInformation("Curriculum seeded from {Count} file(s)", files.Count);
    }

    public async Task SeedAsync(IReadOnlyList<ChapterDoc> docs, CancellationToken ct = default)
    {
        Validate(docs);

        var chapters = await db.Chapters
            .Include(c => c.Lessons).ThenInclude(l => l.Exercises)
            .Include(c => c.Lessons).ThenInclude(l => l.ReviewItems)
            .Include(c => c.Lessons).ThenInclude(l => l.Prerequisites)
            .ToListAsync(ct);

        foreach (var doc in docs)
        {
            var chapter = chapters.FirstOrDefault(c => c.Slug == doc.Slug);
            if (chapter is null)
            {
                chapter = new Chapter { Slug = doc.Slug, Title = doc.Title };
                db.Chapters.Add(chapter);
                chapters.Add(chapter);
            }

            chapter.Number = doc.Number;
            chapter.Title = doc.Title;
            chapter.Subtitle = doc.Subtitle ?? "";
            chapter.Description = doc.Description ?? "";
            chapter.IsPremium = doc.IsPremium;
            chapter.SortOrder = doc.SortOrder ?? doc.Number;

            for (var i = 0; i < doc.Lessons.Count; i++)
            {
                var ld = doc.Lessons[i];
                var lesson = chapter.Lessons.FirstOrDefault(l => l.Slug == ld.Slug);
                if (lesson is null)
                {
                    lesson = new Lesson { Slug = ld.Slug, Title = ld.Title };
                    chapter.Lessons.Add(lesson);
                }

                lesson.Title = ld.Title;
                lesson.Summary = ld.Summary ?? "";
                lesson.Kind = string.Equals(ld.Kind, "practice", StringComparison.OrdinalIgnoreCase) ? LessonKind.Practice : LessonKind.Lesson;
                lesson.SortOrder = i + 1;
                lesson.XpReward = ld.XpReward ?? 10;
                lesson.Icon = ld.Icon ?? "note";

                SyncExercises(lesson, ld.Exercises);
                SyncReviewItems(lesson, ld.ReviewItems ?? []);
            }

            var keep = doc.Lessons.Select(l => l.Slug).ToHashSet();
            foreach (var removed in chapter.Lessons.Where(l => !keep.Contains(l.Slug)).ToList())
            {
                logger.LogWarning("Removing lesson {Slug} no longer present in curriculum", removed.Slug);
                chapter.Lessons.Remove(removed);
                db.Lessons.Remove(removed);
            }
        }

        await db.SaveChangesAsync(ct);

        var ordered = docs.OrderBy(d => d.SortOrder ?? d.Number)
            .SelectMany(d => d.Lessons.Select(l => (Doc: l, Chapter: d)))
            .ToList();
        var bySlug = chapters.SelectMany(c => c.Lessons).ToDictionary(l => l.Slug);

        for (var i = 0; i < ordered.Count; i++)
        {
            var (ld, _) = ordered[i];
            var lesson = bySlug[ld.Slug];
            var wanted = ld.Prerequisites is { } explicitPrereqs
                ? explicitPrereqs.Select(s => bySlug.TryGetValue(s, out var p) ? p.Id : throw new InvalidOperationException($"Unknown prerequisite '{s}' on lesson '{ld.Slug}'")).ToHashSet()
                : i == 0 ? [] : new HashSet<int> { bySlug[ordered[i - 1].Doc.Slug].Id };

            lesson.Prerequisites.RemoveAll(p => !wanted.Contains(p.PrerequisiteLessonId));
            foreach (var id in wanted.Where(id => lesson.Prerequisites.All(p => p.PrerequisiteLessonId != id)))
                lesson.Prerequisites.Add(new LessonPrerequisite { LessonId = lesson.Id, PrerequisiteLessonId = id });
        }

        await db.SaveChangesAsync(ct);
    }

    private void SyncExercises(Lesson lesson, List<ExerciseDoc> docs)
    {
        for (var i = 0; i < docs.Count; i++)
        {
            var ed = docs[i];
            var ex = lesson.Exercises.FirstOrDefault(e => e.Key == ed.Key);
            if (ex is null)
            {
                ex = new Exercise { Key = ed.Key, Type = ed.Type, ContentJson = "{}" };
                lesson.Exercises.Add(ex);
            }
            ex.SortOrder = i + 1;
            ex.Type = ed.Type;
            ex.ContentJson = ed.Content.ToJsonString();
            ex.AnswerJson = ed.Answer?.ToJsonString();
        }

        var keep = docs.Select(d => d.Key).ToHashSet();
        foreach (var removed in lesson.Exercises.Where(e => !keep.Contains(e.Key)).ToList())
        {
            lesson.Exercises.Remove(removed);
            db.Exercises.Remove(removed);
        }
    }

    private void SyncReviewItems(Lesson lesson, List<ReviewItemDoc> docs)
    {
        foreach (var rd in docs)
        {
            var item = lesson.ReviewItems.FirstOrDefault(r => r.Key == rd.Key);
            if (item is null)
            {
                item = new ReviewItem { Key = rd.Key, Front = rd.Front, Back = rd.Back };
                lesson.ReviewItems.Add(item);
            }
            item.Front = rd.Front;
            item.Back = rd.Back;
            item.Hint = rd.Hint;
            item.NotationJson = rd.Notation?.ToJsonString();
        }

        var keep = docs.Select(d => d.Key).ToHashSet();
        foreach (var removed in lesson.ReviewItems.Where(r => !keep.Contains(r.Key)).ToList())
        {
            lesson.ReviewItems.Remove(removed);
            db.ReviewItems.Remove(removed);
        }
    }

    private static void Validate(IReadOnlyList<ChapterDoc> docs)
    {
        var errors = new List<string>();
        var lessonSlugs = new HashSet<string>();
        var reviewKeys = new HashSet<string>();
        foreach (var c in docs)
        {
            if (string.IsNullOrWhiteSpace(c.Slug)) errors.Add("Chapter without slug");
            foreach (var l in c.Lessons)
            {
                if (!lessonSlugs.Add(l.Slug)) errors.Add($"Duplicate lesson slug '{l.Slug}'");
                if (l.Exercises.Count == 0) errors.Add($"Lesson '{l.Slug}' has no exercises");
                var exKeys = new HashSet<string>();
                foreach (var e in l.Exercises)
                {
                    if (!exKeys.Add(e.Key)) errors.Add($"Duplicate exercise key '{e.Key}' in '{l.Slug}'");
                    if (!ExerciseTypes.All.Contains(e.Type)) errors.Add($"Unknown exercise type '{e.Type}' ({l.Slug}/{e.Key})");
                    if (e.Type != ExerciseTypes.Info && e.Answer is null) errors.Add($"Graded exercise {l.Slug}/{e.Key} has no answer");
                }
                foreach (var r in l.ReviewItems ?? [])
                    if (!reviewKeys.Add(r.Key)) errors.Add($"Duplicate review item key '{r.Key}'");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException("Invalid curriculum:\n" + string.Join("\n", errors));
    }

    public record ChapterDoc(string Slug, int Number, string Title, string? Subtitle, string? Description, bool IsPremium, int? SortOrder, List<LessonDoc> Lessons);

    public record LessonDoc(string Slug, string Title, string? Summary, string? Kind, int? XpReward, string? Icon,
        List<string>? Prerequisites, List<ExerciseDoc> Exercises, List<ReviewItemDoc>? ReviewItems);

    public record ExerciseDoc(string Key, string Type, JsonObject Content, JsonObject? Answer);

    public record ReviewItemDoc(string Key, string Front, string Back, string? Hint, JsonObject? Notation);
}

public static class ExerciseTypes
{
    public const string Info = "info";
    public const string MultipleChoice = "multipleChoice";
    public const string Ordering = "ordering";
    public const string Keyboard = "keyboard";
    public static readonly HashSet<string> All = [Info, MultipleChoice, Ordering, Keyboard];
}
