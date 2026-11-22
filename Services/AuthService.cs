using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Improvising_Fugue.Services;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "improvising-fugue";
    public string Audience { get; set; } = "improvising-fugue";

    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public record AuthResult(User User, string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

public class AuthService(AppDbContext db, IOptions<JwtOptions> jwtOptions, TimeProvider clock)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;
    public const int MinPasswordLength = 8;

    public async Task<AuthResult> RegisterAsync(string email, string password, string? displayName, string? timeZoneId, CancellationToken ct)
    {
        email = (email ?? "").Trim();
        if (!IsValidEmail(email)) throw AppException.Validation("Please enter a valid email address.");
        if ((password ?? "").Length < MinPasswordLength) throw AppException.Validation($"Password must be at least {MinPasswordLength} characters.");
        if (password!.Length > 128) throw AppException.Validation("Password is too long.");

        var normalized = email.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
            throw AppException.Conflict("email_taken", "An account with that email already exists.");

        var name = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim();
        if (name.Length > 64) name = name[..64];

        var now = clock.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Email = email,
            NormalizedEmail = normalized,
            PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 11),
            DisplayName = name,
            TimeZoneId = timeZoneId is not null && UserClock.IsValidTimeZone(timeZoneId) ? timeZoneId : "UTC",
            CreatedAt = now,
            Subscription = new Subscription { UpdatedAt = now },
            Streak = new UserStreak(),
        };
        db.Users.Add(user);
        var result = IssueTokens(user, now);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null || !BCrypt.Net.BCrypt.EnhancedVerify(password ?? "", user.PasswordHash))
            throw new AppException(401, "invalid_credentials", "Email or password is incorrect.");

        var result = IssueTokens(user, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<AuthResult> RefreshAsync(string rawToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token?.User is null) throw new AppException(401, "invalid_refresh_token", "Session expired. Please sign in again.");

        if (!token.IsActive(now))
        {
            if (token.ReplacedByTokenId is not null)
            {
                await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            }
            throw new AppException(401, "invalid_refresh_token", "Session expired. Please sign in again.");
        }

        var result = IssueTokens(token.User, now, out var newToken);
        token.RevokedAt = now;
        token.ReplacedByTokenId = newToken.Id;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) return;
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct) ?? throw AppException.NotFound("User");
        if (!BCrypt.Net.BCrypt.EnhancedVerify(currentPassword ?? "", user.PasswordHash))
            throw AppException.Validation("Current password is incorrect.");
        if ((newPassword ?? "").Length < MinPasswordLength) throw AppException.Validation($"Password must be at least {MinPasswordLength} characters.");

        user.PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(newPassword, workFactor: 11);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.SaveChangesAsync(ct);
    }

    private AuthResult IssueTokens(User user, DateTime now) => IssueTokens(user, now, out _);

    private AuthResult IssueTokens(User user, DateTime now, out RefreshToken refreshEntity)
    {
        var accessExpires = now.AddMinutes(_jwt.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var jwt = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            notBefore: now,
            expires: accessExpires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        refreshEntity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.AddDays(_jwt.RefreshTokenDays),
        };
        db.RefreshTokens.Add(refreshEntity);

        return new AuthResult(user, accessToken, accessExpires, raw, refreshEntity.ExpiresAt);
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static bool IsValidEmail(string email)
    {
        if (email.Length is < 3 or > 256) return false;
        return MailAddress.TryCreate(email, out var addr) && addr.Address == email;
    }
}
