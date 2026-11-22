using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<StripeOptions>(config.GetSection(StripeOptions.Section));
builder.Services.Configure<AppOptions>(config.GetSection("App"));
builder.Services.Configure<BillingOptions>(config.GetSection("Billing"));

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be configured (min 32 chars). Set the Jwt__SigningKey environment variable.");

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(config.GetConnectionString("Default") ?? "Data Source=improvising-fugue.db"));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReviewScheduler, Sm2Scheduler>();
builder.Services.AddSingleton<IStripeGateway, StripeGateway>();
builder.Services.AddScoped<CurriculumSeeder>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<CurriculumService>();
builder.Services.AddScoped<LessonService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<BillingService>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();

var corsOrigins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<CurriculumSeeder>()
        .SeedFromDirectoryAsync(Path.Combine(app.Environment.ContentRootPath, "Content", "Curriculum"));
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();
else
    app.UseHttpsRedirection();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

public partial class Program;
