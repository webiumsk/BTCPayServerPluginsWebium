#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SepaInstantQr.Controllers;

[Route("plugins/sepainstantqr")]
public class SepaCheckoutController : Controller
{
    private readonly SepaDbContextFactory _dbContextFactory;

    public SepaCheckoutController(SepaDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
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
}
