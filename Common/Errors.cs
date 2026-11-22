using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Improvising_Fugue.Common;

public class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static AppException NotFound(string what) => new(404, "not_found", $"{what} was not found.");
    public static AppException Validation(string message) => new(400, "validation_failed", message);
    public static AppException Forbidden(string code, string message) => new(403, code, message);
    public static AppException Conflict(string code, string message) => new(409, code, message);
}

public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        int status;
        ProblemDetails problem;
        if (exception is AppException app)
        {
            status = app.StatusCode;
            problem = new ProblemDetails { Status = status, Title = app.Message, Type = app.Code };
            problem.Extensions["code"] = app.Code;
        }
        else
        {
            logger.LogError(exception, "Unhandled exception");
            status = StatusCodes.Status500InternalServerError;
            problem = new ProblemDetails { Status = status, Title = "Something went wrong.", Type = "server_error" };
            problem.Extensions["code"] = "server_error";
        }

        http.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : throw new AppException(401, "unauthorized", "Not signed in.");
    }
}

public static class UserClock
{
    public static DateOnly LocalDate(DateTime utcNow, string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return DateOnly.FromDateTime(utcNow);
    }

    public static bool IsValidTimeZone(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch { return false; }
    }

    public static DateTime StartOfLocalDayUtc(DateOnly date, string? timeZoneId)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return TimeZoneInfo.ConvertTimeToUtc(local, tz);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { }
        }
        return DateTime.SpecifyKind(local, DateTimeKind.Utc);
    }
}
