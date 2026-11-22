using Improvising_Fugue.Common;
using Improvising_Fugue.Data;
using Improvising_Fugue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Improvising_Fugue.Services;

public class AppOptions
{
    public string FrontendUrl { get; set; } = "http://localhost:4200";
}

public class BillingOptions
{
    public bool EnableDevPremiumToggle { get; set; }
}

public class EntitlementService(AppDbContext db, TimeProvider clock)
{
    public async Task<bool> IsPremiumAsync(Guid userId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return IsPremium(sub, clock.GetUtcNow().UtcDateTime);
    }

    public static bool IsPremium(Subscription? sub, DateTime now)
    {
        if (sub is null) return false;
        if (sub.DevGranted) return true;
        return sub.Status switch
        {
            SubscriptionStatus.Active or SubscriptionStatus.Trialing => true,
            SubscriptionStatus.PastDue => sub.CurrentPeriodEnd is { } end && end > now,
            _ => false,
        };
    }

    public static SubscriptionStatus ParseStatus(string? stripeStatus) => stripeStatus switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" => SubscriptionStatus.PastDue,
        "canceled" => SubscriptionStatus.Canceled,
        "unpaid" => SubscriptionStatus.Unpaid,
        "incomplete" => SubscriptionStatus.Incomplete,
        "incomplete_expired" => SubscriptionStatus.IncompleteExpired,
        "paused" => SubscriptionStatus.Paused,
        _ => SubscriptionStatus.None,
    };
}

public record PlanDto(string Id, string Name, string Tier, long? UnitAmount, string? Currency, string? Interval, int? IntervalCount, IReadOnlyList<string> Features);
public record PlansDto(bool BillingConfigured, IReadOnlyList<PlanDto> Plans);
public record SubscriptionDto(string Tier, string Status, DateTime? CurrentPeriodEnd, bool CancelAtPeriodEnd, bool HasBillingAccount, bool BillingConfigured, bool DevToggleAvailable, bool DevGranted);

public class BillingService(AppDbContext db, IStripeGateway stripe, TimeProvider clock, ILogger<BillingService> logger)
{
    public static readonly string[] FreeFeatures = ["Chapter 1: Introduction to Furno", "Daily spaced-repetition reviews", "XP, streaks and progress tracking"];
    public static readonly string[] PremiumFeatures = ["Every chapter as it is released", "Advanced partimento and fugue lessons", "Unlimited practice sessions", "Support ongoing development"];

    public async Task<PlansDto> GetPlansAsync(CancellationToken ct)
    {
        var price = await stripe.GetPremiumPriceAsync(ct);
        return new PlansDto(stripe.IsConfigured,
        [
            new PlanDto("free", "Free", "Free", 0, price?.Currency, null, null, FreeFeatures),
            new PlanDto("premium", price?.ProductName ?? "Premium", "Premium", price?.UnitAmount, price?.Currency, price?.Interval, price?.IntervalCount, PremiumFeatures),
        ]);
    }

    public async Task<SubscriptionDto> GetSubscriptionAsync(Guid userId, bool devToggleAvailable, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var premium = EntitlementService.IsPremium(sub, clock.GetUtcNow().UtcDateTime);
        return new SubscriptionDto(premium ? "Premium" : "Free", (sub?.Status ?? SubscriptionStatus.None).ToString(),
            sub?.CurrentPeriodEnd, sub?.CancelAtPeriodEnd ?? false, sub?.StripeCustomerId is not null, stripe.IsConfigured, devToggleAvailable, sub?.DevGranted ?? false);
    }

    public async Task<string> StartCheckoutAsync(Guid userId, string frontendUrl, CancellationToken ct)
    {
        RequireConfigured();
        var user = await db.Users.Include(u => u.Subscription).FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        var now = clock.GetUtcNow().UtcDateTime;
        if (EntitlementService.IsPremium(user.Subscription, now) && !(user.Subscription?.DevGranted ?? false))
            throw AppException.Conflict("already_premium", "You already have Premium. Use Manage subscription to change it.");

        var sub = user.Subscription ??= new Subscription { UserId = userId, UpdatedAt = now };
        if (sub.StripeCustomerId is null)
        {
            sub.StripeCustomerId = await stripe.CreateCustomerAsync(user.Email, user.Id, ct);
            sub.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var baseUrl = frontendUrl.TrimEnd('/');
        return await stripe.CreateCheckoutSessionAsync(sub.StripeCustomerId, userId,
            $"{baseUrl}/premium?checkout=success", $"{baseUrl}/premium?checkout=cancelled", ct);
    }

    public async Task<string> CreatePortalAsync(Guid userId, string frontendUrl, CancellationToken ct)
    {
        RequireConfigured();
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub?.StripeCustomerId is null) throw AppException.Validation("There is no billing account to manage yet.");
        return await stripe.CreatePortalSessionAsync(sub.StripeCustomerId, $"{frontendUrl.TrimEnd('/')}/account", ct);
    }

    public async Task HandleWebhookAsync(WebhookEnvelope evt, CancellationToken ct)
    {
        if (await db.StripeWebhookEvents.AnyAsync(e => e.Id == evt.EventId, ct))
        {
            logger.LogInformation("Stripe event {Id} already processed", evt.EventId);
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        switch (evt.Type)
        {
            case "checkout.session.completed":
            case "customer.subscription.created":
            case "customer.subscription.updated":
            case "customer.subscription.deleted":
            case "customer.subscription.paused":
            case "customer.subscription.resumed":
            case "invoice.paid":
            case "invoice.payment_succeeded":
            case "invoice.payment_failed":
                await SyncAsync(evt, now, ct);
                break;
            default:
                logger.LogDebug("Ignoring Stripe event type {Type}", evt.Type);
                break;
        }

        db.StripeWebhookEvents.Add(new StripeWebhookEvent { Id = evt.EventId, Type = evt.Type, ProcessedAt = now });
        await db.SaveChangesAsync(ct);
    }

    private async Task SyncAsync(WebhookEnvelope evt, DateTime now, CancellationToken ct)
    {
        var local = await FindLocalSubscriptionAsync(evt, ct);
        if (local is null)
        {
            logger.LogWarning("Stripe event {Id} ({Type}) could not be matched to a user", evt.EventId, evt.Type);
            return;
        }

        if (evt.CustomerId is not null) local.StripeCustomerId ??= evt.CustomerId;

        var subscriptionId = evt.SubscriptionId ?? local.StripeSubscriptionId;
        if (subscriptionId is null) { local.UpdatedAt = now; return; }

        var snapshot = await stripe.GetSubscriptionAsync(subscriptionId, ct);
        if (snapshot is null) return;
        Apply(local, snapshot, now);
    }

    public static void Apply(Subscription local, SubscriptionSnapshot snap, DateTime now)
    {
        local.StripeSubscriptionId = snap.Id;
        local.StripeCustomerId = snap.CustomerId;
        local.StripePriceId = snap.PriceId;
        local.Status = EntitlementService.ParseStatus(snap.Status);
        local.CurrentPeriodEnd = snap.CurrentPeriodEnd;
        local.CancelAtPeriodEnd = snap.CancelAtPeriodEnd;
        local.UpdatedAt = now;
    }

    private async Task<Subscription?> FindLocalSubscriptionAsync(WebhookEnvelope evt, CancellationToken ct)
    {
        if (evt.SubscriptionId is not null)
        {
            var bySub = await db.Subscriptions.FirstOrDefaultAsync(s => s.StripeSubscriptionId == evt.SubscriptionId, ct);
            if (bySub is not null) return bySub;
        }
        if (evt.CustomerId is not null)
        {
            var byCustomer = await db.Subscriptions.FirstOrDefaultAsync(s => s.StripeCustomerId == evt.CustomerId, ct);
            if (byCustomer is not null) return byCustomer;
        }
        if (Guid.TryParse(evt.ClientReferenceId, out var userId))
        {
            var user = await db.Users.Include(u => u.Subscription).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null)
            {
                user.Subscription ??= new Subscription { UserId = userId };
                return user.Subscription;
            }
        }
        return null;
    }

    public async Task SetDevPremiumAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (sub is null)
        {
            sub = new Subscription { UserId = userId };
            db.Subscriptions.Add(sub);
        }
        sub.DevGranted = enabled;
        sub.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private void RequireConfigured()
    {
        if (!stripe.IsConfigured)
            throw new AppException(503, "billing_not_configured",
                "Payments are not configured on this server yet. Set Stripe__SecretKey and Stripe__PremiumPriceId (see README).");
    }
}
