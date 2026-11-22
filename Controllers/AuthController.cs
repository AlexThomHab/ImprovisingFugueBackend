using Improvising_Fugue.Common;
using Improvising_Fugue.Models;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Improvising_Fugue.Controllers;

[AllowAnonymous]
[EnableRateLimiting("auth")]
public class AuthController(AuthService auth, EntitlementService entitlements) : ApiControllerBase
{
    public const string RefreshCookie = "if_refresh";
    private const string CookiePath = "/api/auth";

    [HttpPost("register")]
    public async Task<AuthResponse> Register(RegisterRequest req, CancellationToken ct) =>
        await Respond(await auth.RegisterAsync(req.Email, req.Password, req.DisplayName, req.TimeZone, ct), ct);

    [HttpPost("login")]
    public async Task<AuthResponse> Login(LoginRequest req, CancellationToken ct) =>
        await Respond(await auth.LoginAsync(req.Email, req.Password, ct), ct);

    [HttpPost("refresh")]
    public async Task<AuthResponse> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var raw) || string.IsNullOrEmpty(raw))
            throw new AppException(401, "no_session", "No active session.");
        try
        {
            return await Respond(await auth.RefreshAsync(raw, ct), ct);
        }
        catch (AppException)
        {
            ClearCookie();
            throw;
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie, out var raw);
        await auth.LogoutAsync(raw, ct);
        ClearCookie();
        return NoContent();
    }

    private async Task<AuthResponse> Respond(AuthResult result, CancellationToken ct)
    {
        Response.Cookies.Append(RefreshCookie, result.RefreshToken, CookieOptions(result.RefreshTokenExpiresAt));
        var premium = await entitlements.IsPremiumAsync(result.User.Id, ct);
        return new AuthResponse(result.AccessToken, result.AccessTokenExpiresAt, ProfileDto.From(result.User, premium));
    }

    private void ClearCookie() => Response.Cookies.Delete(RefreshCookie, CookieOptions(null));

    private CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
    };
}
