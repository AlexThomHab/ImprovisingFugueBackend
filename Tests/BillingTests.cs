using Improvising_Fugue.Common;
using Improvising_Fugue.Domain;
using Improvising_Fugue.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImprovisingFugue.Tests;

internal sealed class FakeStripe : IStripeGateway
{
    public bool IsConfigured { get; set; } = true;
    public string? PremiumPriceId => "price_test";
    public Dictionary<string, SubscriptionSnapshot> Subscriptions { get; } = new();
    public int CustomersCreated { get; private set; }

    public Task<string> CreateCustomerAsync(string email, Guid userId, CancellationToken ct) { CustomersCreated++; return Task.FromResult($"cus_{CustomersCreated}"); }
    public Task<string> CreateCheckoutSessionAsync(string customerId, Guid userId, string successUrl, string cancelUrl, CancellationToken ct) => Task.FromResult($"https://checkout.test/{customerId}");
    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct) => Task.FromResult($"https://portal.test/{customerId}");
    public Task<SubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct) => Task.FromResult(Subscriptions.GetValueOrDefault(subscriptionId));
    public Task<PriceInfo?> GetPremiumPriceAsync(CancellationToken ct) => Task.FromResult<PriceInfo?>(null);
    public WebhookEnvelope ParseWebhook(string json, string signatureHeader) => throw new NotSupportedException();
}

public class BillingTests : IDisposable
{
    private readonly TestHarness h = new();
    private readonly FakeStripe stripe = new();
    public void Dispose() => h.Dispose();

    private BillingService Billing => new(h.Db, stripe, h.Clock, NullLogger<BillingService>.Instance);
    private DateTime Now => h.Clock.GetUtcNow().UtcDateTime;

    [Theory]
    [InlineData(SubscriptionStatus.None, false)]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.Trialing, true)]
    [InlineData(SubscriptionStatus.Canceled, false)]
    [InlineData(SubscriptionStatus.Unpaid, false)]
    [InlineData(SubscriptionStatus.Incomplete, false)]
    public void Entitlement_follows_subscription_status(SubscriptionStatus status, bool expected) =>
        Assert.Equal(expected, EntitlementService.IsPremium(new Subscription { Status = status }, Now));

    [Fact]
    public void Past_due_keeps_access_only_until_period_end()
    {
        var sub = new Subscription { Status = SubscriptionStatus.PastDue, CurrentPeriodEnd = Now.AddDays(2) };
        Assert.True(EntitlementService.IsPremium(sub, Now));
        Assert.False(EntitlementService.IsPremium(sub, Now.AddDays(3)));
    }

    [Fact]
    public async Task Checkout_creates_customer_once_and_returns_url()
    {
        var user = await h.CreateUserAsync();
        var url1 = await Billing.StartCheckoutAsync(user.Id, "http://localhost:4200", default);
        var url2 = await Billing.StartCheckoutAsync(user.Id, "http://localhost:4200", default);

        Assert.Equal("https://checkout.test/cus_1", url1);
        Assert.Equal(url1, url2);
        Assert.Equal(1, stripe.CustomersCreated);
    }

    [Fact]
    public async Task Checkout_fails_gracefully_when_stripe_is_not_configured()
    {
        stripe.IsConfigured = false;
        var user = await h.CreateUserAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() => Billing.StartCheckoutAsync(user.Id, "http://x", default));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("billing_not_configured", ex.Code);
    }

    [Fact]
    public async Task Checkout_is_rejected_for_existing_premium_users()
    {
        var user = await h.CreateUserAsync(premium: true);
        var ex = await Assert.ThrowsAsync<AppException>(() => Billing.StartCheckoutAsync(user.Id, "http://x", default));
        Assert.Equal("already_premium", ex.Code);
    }

    [Fact]
    public async Task Webhook_lifecycle_activates_renews_and_cancels()
    {
        var user = await h.CreateUserAsync();
        var periodEnd = Now.AddMonths(1);
        stripe.Subscriptions["sub_1"] = new("sub_1", "cus_9", "active", "price_test", periodEnd, false);

        await Billing.HandleWebhookAsync(new("evt_1", "checkout.session.completed", "cus_9", "sub_1", user.Id.ToString()), default);
        Assert.True(await h.Entitlements.IsPremiumAsync(user.Id, default));
        var sub = await h.NewContext().Subscriptions.SingleAsync(s => s.UserId == user.Id);
        Assert.Equal("cus_9", sub.StripeCustomerId);
        Assert.Equal(periodEnd, sub.CurrentPeriodEnd);

        stripe.Subscriptions["sub_1"] = stripe.Subscriptions["sub_1"] with { CurrentPeriodEnd = periodEnd.AddMonths(1) };
        await Billing.HandleWebhookAsync(new("evt_2", "invoice.paid", "cus_9", null, null), default);
        Assert.Equal(periodEnd.AddMonths(1), (await h.NewContext().Subscriptions.SingleAsync(s => s.UserId == user.Id)).CurrentPeriodEnd);

        stripe.Subscriptions["sub_1"] = stripe.Subscriptions["sub_1"] with { CancelAtPeriodEnd = true };
        await Billing.HandleWebhookAsync(new("evt_3", "customer.subscription.updated", "cus_9", "sub_1", null), default);
        Assert.True(await h.Entitlements.IsPremiumAsync(user.Id, default));

        stripe.Subscriptions["sub_1"] = stripe.Subscriptions["sub_1"] with { Status = "canceled" };
        await Billing.HandleWebhookAsync(new("evt_4", "customer.subscription.deleted", "cus_9", "sub_1", null), default);
        Assert.False(await h.Entitlements.IsPremiumAsync(user.Id, default));
    }

    [Fact]
    public async Task Duplicate_webhook_events_are_processed_once()
    {
        var user = await h.CreateUserAsync();
        stripe.Subscriptions["sub_1"] = new("sub_1", "cus_9", "active", "price_test", Now.AddMonths(1), false);
        await Billing.HandleWebhookAsync(new("evt_1", "checkout.session.completed", "cus_9", "sub_1", user.Id.ToString()), default);

        stripe.Subscriptions["sub_1"] = stripe.Subscriptions["sub_1"] with { Status = "canceled" };
        await Billing.HandleWebhookAsync(new("evt_1", "checkout.session.completed", "cus_9", "sub_1", user.Id.ToString()), default);

        Assert.True(await h.Entitlements.IsPremiumAsync(user.Id, default));
        Assert.Equal(1, await h.Db.StripeWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task Unmatched_webhook_is_recorded_without_touching_users()
    {
        await h.CreateUserAsync();
        await Billing.HandleWebhookAsync(new("evt_x", "customer.subscription.updated", "cus_unknown", "sub_unknown", null), default);
        Assert.Equal(0, await h.Db.Subscriptions.CountAsync(s => s.StripeSubscriptionId != null));
        Assert.Equal(1, await h.Db.StripeWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task Premium_from_webhook_unlocks_premium_lessons()
    {
        var user = await h.CreateUserAsync();
        await h.Lessons.CompleteAsync(user.Id, h.Lesson("l1").Id, h.Answers("l1"), default);
        await h.Lessons.CompleteAsync(user.Id, h.Lesson("l2").Id, h.Answers("l2"), default);
        await Assert.ThrowsAsync<AppException>(() => h.Lessons.GetForPlayAsync(user.Id, "p1", default));

        stripe.Subscriptions["sub_1"] = new("sub_1", "cus_9", "active", "price_test", Now.AddMonths(1), false);
        await Billing.HandleWebhookAsync(new("evt_1", "checkout.session.completed", "cus_9", "sub_1", user.Id.ToString()), default);

        Assert.Equal(LessonState.Available, (await h.Lessons.GetForPlayAsync(user.Id, "p1", default)).State);
    }
}
