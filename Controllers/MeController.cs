using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Models;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Controllers;

public class MeController(AppDbContext db, AuthService auth, EntitlementService entitlements) : ApiControllerBase
{
    private static readonly int[] AllowedGoals = [10, 20, 30, 50];

    [HttpGet]
    public async Task<ProfileDto> Get(CancellationToken ct)
    {
        var user = await db.Users.FindAsync([UserId], ct) ?? throw AppException.NotFound("User");
        return ProfileDto.From(user, await entitlements.IsPremiumAsync(UserId, ct));
    }

    [HttpPut]
    public async Task<ProfileDto> Update(UpdateProfileRequest req, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([UserId], ct) ?? throw AppException.NotFound("User");

        if (req.DisplayName is not null)
        {
            var name = req.DisplayName.Trim();
            if (name.Length is < 1 or > 64) throw AppException.Validation("Display name must be 1–64 characters.");
            user.DisplayName = name;
        }
        if (req.DailyGoalXp is { } goal)
        {
            if (!AllowedGoals.Contains(goal)) throw AppException.Validation($"Daily goal must be one of {string.Join(", ", AllowedGoals)}.");
            user.DailyGoalXp = goal;
        }
        if (req.TimeZone is not null)
        {
            if (!UserClock.IsValidTimeZone(req.TimeZone)) throw AppException.Validation("Unknown time zone.");
            user.TimeZoneId = req.TimeZone;
        }
        if (req.SoundEnabled is { } sound) user.SoundEnabled = sound;

        await db.SaveChangesAsync(ct);
        return ProfileDto.From(user, await entitlements.IsPremiumAsync(UserId, ct));
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(UserId, req.CurrentPassword, req.NewPassword, ct);
        return NoContent();
    }
}
