using Improvising_Fugue.Domain;

namespace Improvising_Fugue.Services;

public record ReviewSchedule(double EaseFactor, double IntervalDays, int Repetitions, int Lapses, DateTime DueAt);

public interface IReviewScheduler
{
    ReviewSchedule Next(UserReviewState current, ReviewRating rating, DateTime now);
}

public sealed class Sm2Scheduler : IReviewScheduler
{
    public const double MinEase = 1.3;
    public const double MaxIntervalDays = 365;
    public static readonly TimeSpan RelearnDelay = TimeSpan.FromMinutes(10);

    public ReviewSchedule Next(UserReviewState s, ReviewRating rating, DateTime now)
    {
        var ease = s.EaseFactor <= 0 ? 2.5 : s.EaseFactor;
        var prev = s.IntervalDays;
        var n = s.Repetitions;

        switch (rating)
        {
            case ReviewRating.Again:
            {
                ease = Math.Max(MinEase, ease - 0.20);
                return new ReviewSchedule(ease, 0, 0, s.Lapses + 1, now + RelearnDelay);
            }
            case ReviewRating.Hard:
            {
                ease = Math.Max(MinEase, ease - 0.15);
                var interval = n == 0 ? 1 : Math.Max(prev + 1, prev * 1.2);
                return Make(ease, interval, n + 1, s.Lapses, now);
            }
            case ReviewRating.Good:
            {
                return Make(ease, GoodInterval(prev, n, ease), n + 1, s.Lapses, now);
            }
            case ReviewRating.Easy:
            {
                ease += 0.15;
                var interval = n == 0 ? 4 : Math.Max(GoodInterval(prev, n, ease) + 1, prev * ease * 1.3);
                return Make(ease, interval, n + 1, s.Lapses, now);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(rating));
        }
    }

    private static double GoodInterval(double prev, int n, double ease) => n switch
    {
        0 => 1,
        1 => 3,
        _ => Math.Max(prev + 1, prev * ease),
    };

    private static ReviewSchedule Make(double ease, double interval, int reps, int lapses, DateTime now)
    {
        var days = Math.Min(MaxIntervalDays, Math.Round(interval));
        return new ReviewSchedule(Math.Round(ease, 2), days, reps, lapses, now.AddDays(days));
    }
}
