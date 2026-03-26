#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

[Route("plugins/cashumelt")]
public class CashuMeltCheckoutController : Controller
{
    private readonly CashuMeltPaymentService _paymentService;

    public CashuMeltCheckoutController(CashuMeltPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// Checkout polling endpoint – called by the Vue.js frontend every 2 seconds.
    /// GET /plugins/cashumelt/poll/{quoteId}
    /// Returns { "paid": true/false, "error": "..." }
    /// </summary>
    [HttpGet("poll/{quoteId}")]
    public async Task<IActionResult> Poll(string quoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            return BadRequest(new { paid = false, error = "Quote ID is required" });

        var (paid, error) = await _paymentService.CheckAndRecordPaymentAsync(quoteId, cancellationToken);

        return Ok(new { paid, error });
    }
}
