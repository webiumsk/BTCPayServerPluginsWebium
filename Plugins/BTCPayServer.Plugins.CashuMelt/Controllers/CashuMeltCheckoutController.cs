#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

[Route("plugins/cashumelt")]
public class CashuMeltCheckoutController : Controller
{
    private readonly CashuMeltPaymentService _paymentService;
    private readonly ILogger<CashuMeltCheckoutController> _logger;

    public CashuMeltCheckoutController(
        CashuMeltPaymentService paymentService,
        ILogger<CashuMeltCheckoutController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    /// <summary>
    /// Checkout polling endpoint – called by the Vue.js frontend every 2 seconds.
    /// GET /plugins/cashumelt/poll/{quoteId}
    /// Returns { "paid": bool, "error": string|null, "retryAfterSeconds": number|null }.
    /// When the mint rate-limits (HTTP 429), <c>paid</c> is false, <c>error</c> is null, and <c>retryAfterSeconds</c> suggests backoff.
    /// </summary>
    [HttpGet("poll/{quoteId}")]
    public async Task<IActionResult> Poll(string quoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            return BadRequest(new { paid = false, error = "Quote ID is required" });

        try
        {
            var (paid, error, retryAfterSeconds) =
                await _paymentService.CheckAndRecordPaymentAsync(quoteId, cancellationToken);
            return Ok(new { paid, error, retryAfterSeconds });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "cashumelt_poll_unhandled quote={QuoteId} — returning 200 with backoff to avoid 500 spam",
                quoteId);
            return Ok(new { paid = false, error = (string?)null, retryAfterSeconds = 5 });
        }
    }
}
