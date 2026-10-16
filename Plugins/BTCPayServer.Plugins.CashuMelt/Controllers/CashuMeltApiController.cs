#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Errors;
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
/// Integrators: see docs/AGENT_API.md for <c>retryAfterSeconds</c> and <c>MELT_COMPLETE</c> behavior on retry.
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
    private readonly CashuMeltLightningAddressValidator _lightningAddressValidator;

    public CashuMeltApiController(
        CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory,
        CashuMeltPaymentService paymentService,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        CashuMeltLightningAddressValidator lightningAddressValidator)
    {
        _configService    = configService;
        _dbContextFactory = dbContextFactory;
        _paymentService   = paymentService;
        _storeRepository  = storeRepository;
        _handlers         = handlers;
        _lightningAddressValidator = lightningAddressValidator;
    }

    // ── Settings ───────────────────────────────────────────────────────────────

    /// <summary>GET /api/v1/stores/{storeId}/plugins/cashumelt/settings</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(string storeId, CancellationToken ct = default)
    {
        var s = await _configService.GetSettingsAsync(storeId, ct);
        if (s is null)
            return Ok(new CashuMeltSettingsResponse(
                storeId, null, "sat", null, false, null, null, null));

        return Ok(ToSettingsResponse(s));
    }

    /// <summary>PUT /api/v1/stores/{storeId}/plugins/cashumelt/settings</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        string storeId,
        CancellationToken ct = default)
    {
        // Read JSON manually: [FromBody] JsonElement does not bind reliably on PUT for plugin
        // controllers (ValueKind stays Undefined even when the client sends a valid object).
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Request body must be valid JSON" });
        }

        if (body.ValueKind is not JsonValueKind.Object)
            return BadRequest(new { error = "Request body must be a JSON object" });

        if (!body.TryGetProperty("mintUrl", out var mintUrlEl) || mintUrlEl.ValueKind != JsonValueKind.String)
            return BadRequest(new { error = "mintUrl is required" });
        var mintUrl = (mintUrlEl.GetString() ?? "").Trim().TrimEnd('/');
        if (!mintUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "MintUrl must use HTTPS" });

        if (!body.TryGetProperty("lightningAddress", out var lnEl) || lnEl.ValueKind != JsonValueKind.String)
            return BadRequest(new { error = "lightningAddress is required" });
        var lightningAddress = lnEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(lightningAddress) || !lightningAddress.Contains('@'))
            return BadRequest(new { error = "LightningAddress must be in user@domain format" });

        var unit = "sat";
        if (body.TryGetProperty("unit", out var unitEl) && unitEl.ValueKind == JsonValueKind.String)
            unit = unitEl.GetString() ?? "sat";
        if (unit != "sat" && unit != "usd")
            return BadRequest(new { error = "Unit must be 'sat' or 'usd'" });

        var settings = await _configService.GetSettingsAsync(storeId, ct)
            ?? new CashuMeltStoreSettings { StoreId = storeId };

        settings.MintUrl = mintUrl;
        settings.Unit = unit;
        settings.LightningAddress = lightningAddress.Trim();
        if (body.TryGetProperty("enabled", out var enEl) && enEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            settings.Enabled = enEl.GetBoolean();

        if (body.TryGetProperty("trustedMintUrls", out var tmEl))
        {
            settings.TrustedMintUrls = tmEl.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(tmEl.GetString()) ? null : tmEl.GetString()!.Trim(),
                JsonValueKind.Null => null,
                _ => settings.TrustedMintUrls
            };
        }

        if (body.TryGetProperty("maxMeltFeeReserveSats", out var maxSatsEl))
        {
            settings.MaxMeltFeeReserveSats = maxSatsEl.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when maxSatsEl.TryGetInt64(out var v) => v,
                _ => settings.MaxMeltFeeReserveSats
            };
        }

        if (body.TryGetProperty("maxMeltFeeReservePercentOfMinted", out var maxPctEl))
        {
            settings.MaxMeltFeeReservePercentOfMinted = maxPctEl.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when maxPctEl.TryGetDecimal(out var p) => p,
                _ => settings.MaxMeltFeeReservePercentOfMinted
            };
        }

        try
        {
            CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(settings);
            CashuMeltSettingsValidation.ValidateOptionalFeeCaps(
                settings.MaxMeltFeeReserveSats,
                settings.MaxMeltFeeReservePercentOfMinted);

            if (settings.Enabled)
                await _lightningAddressValidator.ValidateForPayoutAsync(settings.LightningAddress!, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        try { await _configService.SaveSettingsAsync(settings, ct); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }

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

        return Ok(ToSettingsResponse(settings));
    }

    private static CashuMeltSettingsResponse ToSettingsResponse(CashuMeltStoreSettings s) =>
        new(
            s.StoreId,
            s.MintUrl,
            s.Unit,
            s.LightningAddress,
            s.Enabled,
            s.TrustedMintUrls,
            s.MaxMeltFeeReserveSats,
            s.MaxMeltFeeReservePercentOfMinted);

    [HttpGet("safety")]
    public async Task<IActionResult> SettlementSafety(string storeId, CancellationToken ct)
    {
        var counts = await _configService.GetPaymentSafetyCountsAsync(storeId, ct);
        var change = await _configService.GetUnresolvedChangeCountAsync(storeId, ct);
        return Ok(new { safeToChangeWallet = counts.UnresolvedPayments == 0 && change == 0,
            unresolvedPayments = counts.UnresolvedPayments, unresolvedChange = change,
            failedPayments = counts.FailedPayments, otherNonTerminalPayments = counts.OtherNonTerminalPayments,
            terminalRecoveryPayments = counts.TerminalRecoveryPayments });
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
        var storeSettings = await _configService.GetSettingsAsync(storeId, ct);
        var mintBase = CashuMeltMintPolicy.NormalizeMintUrl(storeSettings?.MintUrl ?? "");

        var query = ctx.CashuMeltPaymentRequests.Where(r => r.StoreId == storeId);
        if (!string.IsNullOrWhiteSpace(settlementState))
            query = query.Where(r => r.SettlementState == settlementState.ToUpperInvariant());

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(r => new
            {
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
                r.SettledAt,
                r.RetryCount,
                r.NeedsManualReview,
                r.FailureReasonCode,
                HasOriginalSettings = r.SettingsSnapshotJson != null,
                HasProofs = r.MintedProofsJson != null,
                HasIssuance = r.MintIssuanceJson != null,
                HasChangeOutputs = r.BlankOutputsJson != null,
                HasPayoutQuote = r.MeltQuoteId != null,
                OriginalSettingsRequired = r.SettlementState == "MANUAL_REVIEW" && r.SettingsSnapshotJson == null
            })
            .ToListAsync(ct);

        var items = rows
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
                r.SettledAt,
                null,
                r.RetryCount,
                r.NeedsManualReview,
                r.FailureReasonCode,
                r.OriginalSettingsRequired,
                CashuMeltPaymentSafety.Describe(new(r.SettlementState, r.HasOriginalSettings, r.HasProofs,
                    r.HasIssuance, r.HasChangeOutputs, r.HasPayoutQuote))))
            .ToList();

        return Ok(new { total, offset, limit, items });
    }

    /// <summary>GET /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}</summary>
    [HttpGet("payments/{quoteId}")]
    public async Task<IActionResult> GetPayment(
        string storeId, string quoteId, CancellationToken ct = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var r = await ctx.CashuMeltPaymentRequests.AsNoTracking()
            .Where(x => x.QuoteId == quoteId && x.StoreId == storeId)
            .Select(r => new { r.QuoteId, r.InvoiceId, r.AmountSats, r.Unit, r.State, r.SettlementState,
                r.SettlementError, r.SettlementReference, r.CreatedAt, r.PaidAt, r.SettledAt,
                r.RetryCount, r.NeedsManualReview, r.FailureReasonCode,
                HasOriginalSettings = r.SettingsSnapshotJson != null, HasProofs = r.MintedProofsJson != null,
                HasIssuance = r.MintIssuanceJson != null, HasChangeOutputs = r.BlankOutputsJson != null,
                HasPayoutQuote = r.MeltQuoteId != null }).FirstOrDefaultAsync(ct);

        if (r is null) return NotFound(new { error = "Payment request not found" });

        return Ok(new CashuMeltPaymentResponse(
            r.QuoteId, r.InvoiceId, r.AmountSats, r.Unit,
            r.State, r.SettlementState, r.SettlementError, r.SettlementReference,
            r.CreatedAt, r.PaidAt, r.SettledAt, null,
            r.RetryCount, r.NeedsManualReview, r.FailureReasonCode,
            r.SettlementState == "MANUAL_REVIEW" && !r.HasOriginalSettings,
            CashuMeltPaymentSafety.Describe(new(r.SettlementState, r.HasOriginalSettings, r.HasProofs,
                r.HasIssuance, r.HasChangeOutputs, r.HasPayoutQuote))));
    }

    /// <summary>
    /// POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/retry
    /// Retries settlement/funding verification for <c>PENDING</c> and <c>FAILED</c>, or BTCPay-only when <c>MELT_COMPLETE</c>.
    /// Response includes optional <c>retryAfterSeconds</c> (same shape as checkout poll; see docs/AGENT_API.md).
    /// </summary>
    [HttpPost("payments/{quoteId}/retry")]
    public async Task<IActionResult> RetryPayment(
        string storeId, string quoteId, CancellationToken ct = default)
    {
        var outcome = await _paymentService.RetrySettlementAsync(storeId, quoteId, ct);
        return outcome.Kind switch
        {
            CashuMeltRetryKind.NotFound => NotFound(new { error = "Payment request not found" }),
            CashuMeltRetryKind.AlreadySettled => Ok(new { settled = true, error = (string?)null, retryAfterSeconds = (int?)null }),
            CashuMeltRetryKind.CannotRetryMissingProofs => BadRequest(new { error = "Cannot retry: proofs are not available (already spent or never minted)" }),
            CashuMeltRetryKind.Completed => Ok(new { settled = outcome.Settled, error = outcome.Error, retryAfterSeconds = outcome.RetryAfterSeconds }),
            _ => StatusCode(500, new { error = "Unexpected retry outcome" })
        };
    }

    /// <summary>
    /// Confirms a legacy payment's original mint and payout address without changing store settings.
    /// The mint must identify the exact original funding invoice; settlement is retried separately.
    /// </summary>
    [HttpPost("payments/{quoteId}/confirm-original")]
    [Consumes("application/json")]
    public async Task<IActionResult> ConfirmOriginalSettings(string storeId, string quoteId, CancellationToken ct = default)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(Request.Body, cancellationToken: ct); }
        catch (JsonException) { return BadRequest(new { error = "Request body must be valid JSON" }); }
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("confirmedOriginal", out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
            return BadRequest(new { error = "Confirm that these are the original payment settings" });
        if (!body.TryGetProperty("mintUrl", out var mint) || mint.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(mint.GetString())
            || !body.TryGetProperty("lightningAddress", out var address) || address.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(address.GetString()))
            return BadRequest(new { error = "Original mint URL and payout Lightning Address are required" });
        await using (var ctx = _dbContextFactory.CreateContext())
            if (!await ctx.CashuMeltPaymentRequests.AnyAsync(r => r.StoreId == storeId && r.QuoteId == quoteId, ct))
                return NotFound(new { error = "Payment request not found" });
        try
        {
            await _paymentService.ConfirmOriginalSettingsAsync(storeId, quoteId, mint.GetString()!, address.GetString()!, ct);
            return Ok(new { confirmed = true });
        }
        catch (CashuMeltRecoveryUnavailableException ex)
        { return StatusCode(503, new { error = ex.Message, retryAfterSeconds = ex.RetryAfterSeconds }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(503, new { error = "Original mint or payout provider is unavailable; try again later" }); }
        catch (JsonException) { return StatusCode(503, new { error = "Original mint or payout provider returned invalid verification data" }); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(504, new { error = "Original payment verification timed out; try again later" }); }
    }

    // ── DTOs ────────────────────────────────────────────────────────────────────

    public record CashuMeltSettingsResponse(
        string StoreId,
        string? MintUrl,
        string? Unit,
        string? LightningAddress,
        bool Enabled,
        string? TrustedMintUrls,
        long? MaxMeltFeeReserveSats,
        decimal? MaxMeltFeeReservePercentOfMinted);

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
        DateTimeOffset? SettledAt,
        string? MintQuotePollUrl,
        int RetryCount,
        bool NeedsManualReview,
        string? FailureReasonCode,
        bool OriginalSettingsRequired = false,
        [property: System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] CashuMeltPaymentSafety.Diagnostic? Diagnostic = null)
    {
        public bool BlocksWalletChange => Diagnostic?.BlocksWalletChange ?? false;
        public string[] WalletChangeBlockReasons => Diagnostic?.WalletChangeBlockReasons ?? [];
        public string RecoveryPhase => Diagnostic?.RecoveryPhase ?? "funding_verification";
    }
}
