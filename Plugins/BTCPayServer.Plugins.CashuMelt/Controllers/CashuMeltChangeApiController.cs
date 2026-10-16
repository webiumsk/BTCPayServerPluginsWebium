#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

/// <summary>Merchant recovery assets are available only through store-scoped Greenfield authentication.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.GreenfieldAPIKeys)]
[Authorize(Policy = Policies.CanModifyStoreSettings)]
[Route("api/v1/stores/{storeId}/plugins/cashumelt/change")]
public sealed class CashuMeltChangeApiController(CashuMeltPaymentService paymentService) : Controller
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        SetPrivateHeaders();
        base.OnActionExecuting(context);
    }

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "private, no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
    }

    [HttpGet]
    public async Task<IActionResult> Summary(string storeId, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        try { return Ok(await paymentService.GetChangeSummaryAsync(storeId, ct)); }
        catch (Exception) { return Unavailable(); }
    }

    [HttpPost("export")]
    [Consumes("application/json")]
    public async Task<IActionResult> Export(string storeId, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (!await IsConfirmedAsync(ct)) return ConfirmationRequired();
        try { return Ok(new { tokens = await paymentService.ExportChangeAsync(storeId, ct) }); }
        catch (InvalidOperationException) { return Conflict(new { code = "cashu_change_export_blocked", error = "Change export is not currently available. Recovery evidence has been retained." }); }
        catch (Exception) { return Unavailable(); }
    }

    [HttpPost("verify")]
    [Consumes("application/json")]
    public async Task<IActionResult> Verify(string storeId, CancellationToken ct = default)
    {
        SetPrivateHeaders();
        if (!await IsConfirmedAsync(ct)) return ConfirmationRequired();
        try
        {
            await paymentService.ConfirmChangeRecoveredAsync(storeId, ct);
            return Ok(new { verified = true });
        }
        catch (InvalidOperationException) { return Conflict(new { code = "cashu_change_verification_blocked", error = "Change consumption could not be verified. Recovery evidence has been retained." }); }
        catch (Exception) { return Unavailable(); }
    }

    private async Task<bool> IsConfirmedAsync(CancellationToken ct)
    {
        if (!Request.HasJsonContentType()) return false;
        try
        {
            using var body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            if (body.RootElement.ValueKind != JsonValueKind.Object) return false;
            var count = 0;
            foreach (var property in body.RootElement.EnumerateObject())
            {
                // Reject duplicate/unknown properties, including conflicting confirmations.
                if (property.Name != "confirmed" || property.Value.ValueKind != JsonValueKind.True) return false;
                count++;
            }
            return count == 1;
        }
        catch (JsonException) { return false; }
        catch (Exception) { return false; }
    }

    private BadRequestObjectResult ConfirmationRequired() => BadRequest(new {
        code = "cashu_change_confirmation_required", error = "A JSON request with confirmed set to true is required." });

    private ObjectResult Unavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new {
        code = "cashu_change_unavailable", error = "Change recovery is temporarily unavailable. Recovery evidence has been retained." });
}
