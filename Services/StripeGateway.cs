using Microsoft.Extensions.Options;
using Stripe;

namespace Improvising_Fugue.Services;

public class StripeOptions
{
    public const string Section = "Stripe";

    public string? SecretKey { get; set; }

    public string? WebhookSecret { get; set; }

    public string? PremiumPriceId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey) && !SecretKey.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(PremiumPriceId) && !PremiumPriceId.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}

public record SubscriptionSnapshot(string Id, string CustomerId, string Status, string? PriceId, DateTime? CurrentPeriodEnd, bool CancelAtPeriodEnd);

public record PriceInfo(string Id, long? UnitAmount, string Currency, string? Interval, int? IntervalCount, string? ProductName);

public record WebhookEnvelope(string EventId, string Type, string? CustomerId, string? SubscriptionId, string? ClientReferenceId);

public interface IStripeGateway
{
    bool IsConfigured { get; }
    string? PremiumPriceId { get; }
    Task<string> CreateCustomerAsync(string email, Guid userId, CancellationToken ct);
    Task<string> CreateCheckoutSessionAsync(string customerId, Guid userId, string successUrl, string cancelUrl, CancellationToken ct);
    Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct);
    Task<SubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);
    Task<PriceInfo?> GetPremiumPriceAsync(CancellationToken ct);

    WebhookEnvelope ParseWebhook(string json, string signatureHeader);
}

public class StripeGateway(IOptions<StripeOptions> options, ILogger<StripeGateway> logger) : IStripeGateway
{
    private readonly StripeOptions _opt = options.Value;
    private StripeClient Client => new(_opt.SecretKey);

    public bool IsConfigured => _opt.IsConfigured;
    public string? PremiumPriceId => _opt.PremiumPriceId;

    public async Task<string> CreateCustomerAsync(string email, Guid userId, CancellationToken ct)
    {
        var customer = await new CustomerService(Client).CreateAsync(new CustomerCreateOptions
        {
            Email = email,
            Metadata = new() { ["userId"] = userId.ToString() },
        }, cancellationToken: ct);
        return customer.Id;
    }

    public async Task<string> CreateCheckoutSessionAsync(string customerId, Guid userId, string successUrl, string cancelUrl, CancellationToken ct)
    {
        var session = await new Stripe.Checkout.SessionService(Client).CreateAsync(new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            ClientReferenceId = userId.ToString(),
            LineItems = [new Stripe.Checkout.SessionLineItemOptions { Price = _opt.PremiumPriceId, Quantity = 1 }],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            AllowPromotionCodes = true,
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions { Metadata = new() { ["userId"] = userId.ToString() } },
        }, cancellationToken: ct);
        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct)
    {
        var session = await new Stripe.BillingPortal.SessionService(Client).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl,
        }, cancellationToken: ct);
        return session.Url;
    }

    public async Task<SubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            var sub = await new SubscriptionService(Client).GetAsync(subscriptionId, cancellationToken: ct);
            var item = sub.Items?.Data?.FirstOrDefault();
            return new SubscriptionSnapshot(sub.Id, sub.CustomerId, sub.Status, item?.Price?.Id, item?.CurrentPeriodEnd, sub.CancelAtPeriodEnd);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            logger.LogWarning("Stripe subscription {Id} not found", subscriptionId);
            return null;
        }
    }

    public async Task<PriceInfo?> GetPremiumPriceAsync(CancellationToken ct)
    {
        if (!IsConfigured) return null;
        try
        {
            var price = await new PriceService(Client).GetAsync(_opt.PremiumPriceId, new PriceGetOptions { Expand = ["product"] }, cancellationToken: ct);
            return new PriceInfo(price.Id, price.UnitAmount, price.Currency, price.Recurring?.Interval, (int?)price.Recurring?.IntervalCount, price.Product?.Name);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Could not load Stripe price {PriceId}", _opt.PremiumPriceId);
            return null;
        }
    }

    public WebhookEnvelope ParseWebhook(string json, string signatureHeader)
    {
        var evt = EventUtility.ConstructEvent(json, signatureHeader, _opt.WebhookSecret, throwOnApiVersionMismatch: false);
        return evt.Data.Object switch
        {
            Stripe.Checkout.Session s => new(evt.Id, evt.Type, s.CustomerId, s.SubscriptionId, s.ClientReferenceId),
            Subscription sub => new(evt.Id, evt.Type, sub.CustomerId, sub.Id, null),
            Invoice inv => new(evt.Id, evt.Type, inv.CustomerId, inv.Parent?.SubscriptionDetails?.SubscriptionId, null),
            _ => new(evt.Id, evt.Type, null, null, null),
        };
    }
}
