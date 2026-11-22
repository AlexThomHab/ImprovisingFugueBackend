using Improvising_Fugue.Models;
using Improvising_Fugue.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;

namespace Improvising_Fugue.Controllers;

public class BillingController(
    Services.BillingService billing,
    IStripeGateway stripe,
    IOptions<AppOptions> app,
    IOptions<BillingOptions> billingOptions,
    IWebHostEnvironment env,
    ILogger<BillingController> logger) : ApiControllerBase
{
    private bool DevToggleAvailable => env.IsDevelopment() && billingOptions.Value.EnableDevPremiumToggle;

    [AllowAnonymous]
    [HttpGet("plans")]
    public Task<PlansDto> Plans(CancellationToken ct) => billing.GetPlansAsync(ct);

    [HttpGet("subscription")]
    public Task<SubscriptionDto> Subscription(CancellationToken ct) => billing.GetSubscriptionAsync(UserId, DevToggleAvailable, ct);

    [HttpPost("checkout")]
    public async Task<UrlResponse> Checkout(CancellationToken ct) =>
        new(await billing.StartCheckoutAsync(UserId, app.Value.FrontendUrl, ct));

    [HttpPost("portal")]
    public async Task<UrlResponse> Portal(CancellationToken ct) =>
        new(await billing.CreatePortalAsync(UserId, app.Value.FrontendUrl, ct));

    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        var json = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        WebhookEnvelope evt;
        try
        {
            evt = stripe.ParseWebhook(json, Request.Headers["Stripe-Signature"].ToString());
        }
        catch (Exception ex) when (ex is StripeException or ArgumentException)
        {
            logger.LogWarning(ex, "Rejected Stripe webhook");
            return BadRequest();
        }

        await billing.HandleWebhookAsync(evt, ct);
        return Ok();
    }

    [HttpPost("dev/premium")]
    public async Task<IActionResult> DevPremium(DevPremiumRequest req, CancellationToken ct)
    {
        if (!DevToggleAvailable) return NotFound();
        await billing.SetDevPremiumAsync(UserId, req.Enabled, ct);
        return NoContent();
    }
}
