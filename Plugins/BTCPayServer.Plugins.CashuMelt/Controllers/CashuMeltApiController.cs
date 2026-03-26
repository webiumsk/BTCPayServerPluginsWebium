#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

/// <summary>
/// REST API for CashuMelt plugin store management.
/// Auth: Cookie (UI) or API key with CanModifyStoreSettings permission.
/// Base path: /api/v1/stores/{storeId}/plugins/cashumelt
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie + "," + AuthenticationSchemes.Greenfield)]
[Authorize(Policy = Policies.CanModifyStoreSettings)]
[Route("api/v1/stores/{storeId}/plugins/cashumelt")]
public class CashuMeltApiController : ControllerBase
{
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly CashuMeltPaymentService _paymentService;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;

    public CashuMeltApiController(
        CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory,
        CashuMeltPaymentService paymentService,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers)
    {
        _configService    = configService;
        _dbContextFactory = dbContextFactory;
        _paymentService   = paymentService;
        _storeRepository  = storeRepository;
        _handlers         = handlers;
    }

    // ── Settings ───────────────────────────────────────────────────────────────

    /// <summary>GET /api/v1/stores/{storeId}/plugins/cashumelt/settings</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(string storeId, CancellationToken ct = default)
    {
        var s = await _configService.GetSettingsAsync(storeId, ct);
        if (s is null)
            return Ok(new CashuMeltSettingsResponse(storeId, null, "sat", null, false));

        return Ok(new CashuMeltSettingsResponse(
            s.StoreId,
            s.MintUrl,
            s.Unit,
            s.LightningAddress,
            s.Enabled));
    }

    /// <summary>PUT /api/v1/stores/{storeId}/plugins/cashumelt/settings</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        string storeId,
        [FromBody] CashuMeltSettingsRequest body,
        CancellationToken ct = default)
    {
        if (body is null)
            return BadRequest(new { error = "Request body is required" });

        var mintUrl = (body.MintUrl ?? "").Trim().TrimEnd('/');
        if (!mintUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "MintUrl must use HTTPS" });

        if (string.IsNullOrWhiteSpace(body.LightningAddress) || !body.LightningAddress.Contains('@'))
            return BadRequest(new { error = "LightningAddress must be in user@domain format" });

        var unit = body.Unit ?? "sat";
        if (unit != "sat" && unit != "usd")
            return BadRequest(new { error = "Unit must be 'sat' or 'usd'" });

        var settings = await _configService.GetSettingsAsync(storeId, ct)
            ?? new CashuMeltStoreSettings { StoreId = storeId };

        settings.MintUrl          = mintUrl;
        settings.Unit             = unit;
        settings.LightningAddress = body.LightningAddress.Trim();
        settings.Enabled          = body.Enabled ?? settings.Enabled;

        await _configService.SaveSettingsAsync(settings, ct);

        // Register (or update) the CashuMelt payment method on the BTCPay store so that
        // invoice creation finds at least one payment method in DerivationStrategies.
        // Without this, BTCPay throws "No wallet has been linked to your BTCPay Store".
        var store = await _storeRepository.FindStore(storeId);
        if (store is not null && _handlers.Support(CashuMeltPlugin.CashuMeltPaymentMethodId))
        {
            store.SetPaymentMethodConfig(
                _handlers[CashuMeltPlugin.CashuMeltPaymentMethodId],
                new CashuMeltPaymentMethodConfig { Enabled = settings.Enabled });
            await _storeRepository.UpdateStore(store);
        }

        return Ok(new CashuMeltSettingsResponse(
            settings.StoreId,
            settings.MintUrl,
            settings.Unit,
            settings.LightningAddress,
            settings.Enabled));
    }

    // ── Payment requests ────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/v1/stores/{storeId}/plugins/cashumelt/payments
    /// List CashuMelt payment requests for a store, newest first.
    /// Query: ?limit=50&amp;offset=0&amp;settlementState=SETTLED|PENDING|FAILED
    /// </summary>
    [HttpGet("payments")]
    public async Task<IActionResult> ListPayments(
        string storeId,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? settlementState = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        await using var ctx = _dbContextFactory.CreateContext();

        var query = ctx.CashuMeltPaymentRequests.Where(r => r.StoreId == storeId);
        if (!string.IsNullOrWhiteSpace(settlementState))
            query = query.Where(r => r.SettlementState == settlementState.ToUpperInvariant());

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(r => new CashuMeltPaymentResponse(
                r.QuoteId,
                r.InvoiceId,
                r.AmountSats,
                r.Unit,
                r.State,
                r.SettlementState,
                r.SettlementError,
                r.SettlementReference,
                r.CreatedAt,
                r.PaidAt,
                r.SettledAt))
            .ToListAsync(ct);

        return Ok(new { total, offset, limit, items });
    }

    /// <summary>GET /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}</summary>
    [HttpGet("payments/{quoteId}")]
    public async Task<IActionResult> GetPayment(
        string storeId, string quoteId, CancellationToken ct = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var r = await ctx.CashuMeltPaymentRequests
            .FirstOrDefaultAsync(x => x.QuoteId == quoteId && x.StoreId == storeId, ct);

        if (r is null) return NotFound(new { error = "Payment request not found" });

        return Ok(new CashuMeltPaymentResponse(
            r.QuoteId, r.InvoiceId, r.AmountSats, r.Unit,
            r.State, r.SettlementState, r.SettlementError, r.SettlementReference,
            r.CreatedAt, r.PaidAt, r.SettledAt));
    }

    /// <summary>
    /// POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/retry
    /// Manually retry melt for a FAILED payment (e.g. after transient LN routing failure).
    /// </summary>
    [HttpPost("payments/{quoteId}/retry")]
    public async Task<IActionResult> RetryPayment(
        string storeId, string quoteId, CancellationToken ct = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var r = await ctx.CashuMeltPaymentRequests
            .FirstOrDefaultAsync(x => x.QuoteId == quoteId && x.StoreId == storeId, ct);

        if (r is null) return NotFound(new { error = "Payment request not found" });
        if (r.SettlementState == "SETTLED") return Ok(new { settled = true, error = (string?)null });
        if (string.IsNullOrEmpty(r.MintedProofsJson) && r.SettlementState == "FAILED")
            return BadRequest(new { error = "Cannot retry: proofs are not available (already spent or never minted)" });

        // Reset state so CheckAndRecordPaymentAsync will re-attempt
        r.SettlementState = "PENDING";
        r.SettlementError = null;
        await ctx.SaveChangesAsync(ct);

        var (paid, error) = await _paymentService.CheckAndRecordPaymentAsync(quoteId, ct);
        return Ok(new { settled = paid, error });
    }

    // ── DTOs ────────────────────────────────────────────────────────────────────

    public record CashuMeltSettingsResponse(
        string StoreId,
        string? MintUrl,
        string? Unit,
        string? LightningAddress,
        bool Enabled);

    public record CashuMeltSettingsRequest(
        string? MintUrl,
        string? Unit,
        string? LightningAddress,
        bool? Enabled);

    public record CashuMeltPaymentResponse(
        string QuoteId,
        string InvoiceId,
        long AmountSats,
        string Unit,
        string State,
        string SettlementState,
        string? SettlementError,
        string? SettlementReference,
        DateTimeOffset CreatedAt,
        DateTimeOffset? PaidAt,
        DateTimeOffset? SettledAt);
}
