using Improvising_Fugue.Domain;

namespace Improvising_Fugue.Services;

public static class XpRules
{
    public const int PerfectBonus = 5;

    public const int PerReview = 1;

    public const double MasteryScore = 1.0;

    public static int ReplayXp(int lessonXp) => Math.Max(2, lessonXp / 4);
}

public static class StreakRules
{
    public static bool RegisterActivity(UserStreak streak, DateOnly today)
    {
        if (streak.LastActivityDate == today) return false;

        streak.CurrentStreak = streak.LastActivityDate == today.AddDays(-1) ? streak.CurrentStreak + 1 : 1;
        streak.LongestStreak = Math.Max(streak.LongestStreak, streak.CurrentStreak);
        streak.LastActivityDate = today;
        return true;
    }

    public static int Displayed(UserStreak? streak, DateOnly today) =>
        streak?.LastActivityDate is { } last && last >= today.AddDays(-1) ? streak.CurrentStreak : 0;
}
