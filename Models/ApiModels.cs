using System.Text.Json;
using Improvising_Fugue.Domain;

namespace Improvising_Fugue.Models;

public record RegisterRequest(string Email, string Password, string? DisplayName, string? TimeZone);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string AccessToken, DateTime ExpiresAt, ProfileDto User);

public record ProfileDto(Guid Id, string Email, string DisplayName, int DailyGoalXp, string TimeZone, bool SoundEnabled, DateTime CreatedAt, bool IsPremium)
{
    public static ProfileDto From(User u, bool isPremium) =>
        new(u.Id, u.Email, u.DisplayName, u.DailyGoalXp, u.TimeZoneId, u.SoundEnabled, u.CreatedAt, isPremium);
}

public record UpdateProfileRequest(string? DisplayName, int? DailyGoalXp, string? TimeZone, bool? SoundEnabled);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record CheckAnswerRequest(JsonElement Answer);
public record SubmittedAnswer(int ExerciseId, JsonElement Answer);
public record CompleteLessonRequest(List<SubmittedAnswer>? Answers);

public record RateReviewRequest(ReviewRating Rating, bool Practice = false);

public record UrlResponse(string Url);
public record DevPremiumRequest(bool Enabled);
