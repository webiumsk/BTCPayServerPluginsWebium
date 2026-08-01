#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SepaInstantQr.Controllers;

[Route("plugins/sepainstantqr")]
public class SepaCheckoutController : Controller
{
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaConfigService _configService;
    private readonly SepaMatchingService _matchingService;

    public SepaCheckoutController(
        SepaDbContextFactory dbContextFactory,
        SepaConfigService configService,
        SepaMatchingService matchingService)
    {
        _dbContextFactory = dbContextFactory;
        _configService = configService;
        _matchingService = matchingService;
    }

    /// <summary>
    /// Checkout polling endpoint - called by the checkout Vue component every
    /// 2 seconds. Anonymous by design (the checkout page itself is public);
    /// leaks nothing beyond the paid/pending bit of an unguessable reference.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("poll/{reference}")]
    public async Task<IActionResult> Poll(string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 35)
            return BadRequest(new { paid = false, error = "Invalid reference" });

        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var state = await ctx.SepaPaymentRequests
                .AsNoTracking()
                .Where(r => r.Reference == reference)
                .Select(r => r.State)
                .FirstOrDefaultAsync(cancellationToken);

            return Ok(new { paid = state == SepaPaymentRequestState.Confirmed, error = (string?)null });
        }
        catch (Exception)
        {
            // Never 500-spam the checkout; report a soft failure.
            return Ok(new { paid = false, error = "Failed to check payment status" });
        }
    }

    /// <summary>
    /// Merchant "Mark as paid" button in the checkout. Anonymous like the
    /// rest of the checkout page, but gated server-side by the store's
    /// explicit CheckoutConfirmEnabled opt-in (default off) - designed for
    /// counter-top POS devices the merchant controls, never for e-commerce.
    /// Settles through the shared matching path (webhooks fire, POS flips
    /// to paid), identical to the settings-page manual confirmation.
    /// </summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("confirm-checkout/{reference}")]
    public async Task<IActionResult> ConfirmFromCheckout(string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 35)
            return BadRequest(new { ok = false, error = "Invalid reference" });

        await using var ctx = _dbContextFactory.CreateContext();
        var request = await ctx.SepaPaymentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Reference == reference, cancellationToken);
        if (request is null)
            return NotFound(new { ok = false, error = "Unknown reference" });

        var settings = await _configService.GetSettingsAsync(request.StoreId, cancellationToken);
        if (settings is null || !settings.CheckoutConfirmEnabled)
            return NotFound(new { ok = false, error = "Checkout confirmation is not enabled" });

        var outcome = await _matchingService.ProcessAsync(
            "manual:checkout",
            new ConfirmedPayment(reference, request.AmountDue, request.Currency, RawJson: null, DedupKey: null),
            settings.AmountTolerance,
            cancellationToken);

        return outcome is MatchOutcome.Settled or MatchOutcome.Duplicate
            ? Ok(new { ok = true })
            : Ok(new { ok = false, error = $"Could not settle ({outcome})" });
    }
}
