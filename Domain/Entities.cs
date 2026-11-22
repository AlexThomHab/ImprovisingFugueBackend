namespace Improvising_Fugue.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string PasswordHash { get; set; }
    public required string DisplayName { get; set; }

    public int DailyGoalXp { get; set; } = 20;

    public string TimeZoneId { get; set; } = "UTC";

    public bool SoundEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public Subscription? Subscription { get; set; }
    public UserStreak? Streak { get; set; }
    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}

public enum SubscriptionTier { Free, Premium }

public enum SubscriptionStatus
{
    None,
    Incomplete,
    IncompleteExpired,
    Trialing,
    Active,
    PastDue,
    Canceled,
    Unpaid,
    Paused,
}

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? StripePriceId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.None;
    public DateTime? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }

    public bool DevGranted { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class StripeWebhookEvent
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public DateTime ProcessedAt { get; set; }
}

public class Chapter
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public int Number { get; set; }
    public required string Title { get; set; }
    public string Subtitle { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsPremium { get; set; }
    public int SortOrder { get; set; }
    public List<Lesson> Lessons { get; set; } = [];
}

public enum LessonKind { Lesson, Practice }

public class Lesson
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public Chapter? Chapter { get; set; }

    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string Summary { get; set; } = "";
    public LessonKind Kind { get; set; }
    public int SortOrder { get; set; }
    public int XpReward { get; set; } = 10;
    public string Icon { get; set; } = "note";

    public List<Exercise> Exercises { get; set; } = [];
    public List<LessonPrerequisite> Prerequisites { get; set; } = [];
    public List<ReviewItem> ReviewItems { get; set; } = [];
}

public class LessonPrerequisite
{
    public int LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public int PrerequisiteLessonId { get; set; }
    public Lesson? PrerequisiteLesson { get; set; }
}

public class Exercise
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    public required string Key { get; set; }
    public int SortOrder { get; set; }

    public required string Type { get; set; }

    public required string ContentJson { get; set; }

    public string? AnswerJson { get; set; }

    public bool IsGraded => AnswerJson is not null;
}

public class ReviewItem
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public int LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public required string Front { get; set; }
    public required string Back { get; set; }
    public string? Hint { get; set; }
    public string? NotationJson { get; set; }
}

public class UserLessonProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int LessonId { get; set; }
    public Lesson? Lesson { get; set; }

    public DateTime FirstCompletedAt { get; set; }
    public DateTime LastCompletedAt { get; set; }
    public int CompletionCount { get; set; }
    public double BestScore { get; set; }
    public bool IsMastered { get; set; }
}

public class UserExerciseAttempt
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public int ExerciseId { get; set; }
    public Exercise? Exercise { get; set; }
    public required string AnswerJson { get; set; }
    public bool IsCorrect { get; set; }
    public DateTime AttemptedAt { get; set; }
}

public class UserReviewState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int ReviewItemId { get; set; }
    public ReviewItem? ReviewItem { get; set; }

    public double EaseFactor { get; set; } = 2.5;
    public double IntervalDays { get; set; }
    public int Repetitions { get; set; }
    public int Lapses { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? LastReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<ReviewLog> Logs { get; set; } = [];
}

public enum ReviewRating { Again = 1, Hard = 2, Good = 3, Easy = 4 }

public class ReviewLog
{
    public long Id { get; set; }
    public Guid UserReviewStateId { get; set; }
    public Guid UserId { get; set; }
    public ReviewRating Rating { get; set; }
    public DateTime ReviewedAt { get; set; }
    public double PreviousIntervalDays { get; set; }
    public double NewIntervalDays { get; set; }

    public bool IsPractice { get; set; }
}

public enum XpSource { LessonCompleted, LessonReplayed, PerfectBonus, Review }

public class XpTransaction
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public int Amount { get; set; }
    public XpSource Source { get; set; }
    public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserStreak
{
    public Guid UserId { get; set; }
    public int CurrentStreak { get; set; }
    public int LongestStreak { get; set; }
    public DateOnly? LastActivityDate { get; set; }
}
