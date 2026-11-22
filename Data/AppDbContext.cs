using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<StripeWebhookEvent> StripeWebhookEvents => Set<StripeWebhookEvent>();

    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonPrerequisite> LessonPrerequisites => Set<LessonPrerequisite>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();

    public DbSet<UserLessonProgress> LessonProgress => Set<UserLessonProgress>();
    public DbSet<UserExerciseAttempt> ExerciseAttempts => Set<UserExerciseAttempt>();
    public DbSet<UserReviewState> ReviewStates => Set<UserReviewState>();
    public DbSet<ReviewLog> ReviewLogs => Set<ReviewLog>();
    public DbSet<XpTransaction> XpTransactions => Set<XpTransaction>();
    public DbSet<UserStreak> Streaks => Set<UserStreak>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(64);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasOne(x => x.Subscription).WithOne(x => x.User).HasForeignKey<Subscription>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Streak).WithOne().HasForeignKey<UserStreak>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Subscription>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.StripeCustomerId);
            e.HasIndex(x => x.StripeSubscriptionId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        });

        b.Entity<Chapter>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasMany(x => x.Lessons).WithOne(x => x.Chapter).HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Lesson>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Exercises).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ReviewItems).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LessonPrerequisite>(e =>
        {
            e.HasKey(x => new { x.LessonId, x.PrerequisiteLessonId });
            e.HasOne(x => x.Lesson).WithMany(x => x.Prerequisites).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PrerequisiteLesson).WithMany().HasForeignKey(x => x.PrerequisiteLessonId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Exercise>(e =>
        {
            e.HasIndex(x => new { x.LessonId, x.Key }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(32);
        });

        b.Entity<ReviewItem>(e => e.HasIndex(x => x.Key).IsUnique());

        b.Entity<UserLessonProgress>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.LessonId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Lesson).WithMany().HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserExerciseAttempt>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ExerciseId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserReviewState>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReviewItemId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.DueAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ReviewItem).WithMany().HasForeignKey(x => x.ReviewItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Logs).WithOne().HasForeignKey(x => x.UserReviewStateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReviewLog>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReviewedAt });
            e.Property(x => x.Rating).HasConversion<string>().HasMaxLength(8);
        });

        b.Entity<XpTransaction>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserStreak>(e => e.HasKey(x => x.UserId));

        b.Entity<StripeWebhookEvent>(e => e.Property(x => x.Id).HasMaxLength(255));

        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
            prop.SetValueConverter(utc);
    }
}
