using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Errors;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Orchestrates the CashuMelt mint→melt payment flow:
///   1. Customer pays Lightning invoice created by the CashuMelt mint (quote).
///   2. Plugin detects payment (quote state PAID/ISSUED).
///   3. Plugin mints CashuMelt tokens (NUT-05) – gets blind signatures, unblinds to proofs.
///   4. Plugin immediately melts the proofs (NUT-14) to the merchant's Lightning address.
///   5. Only after a successful melt (merchant received via mint) is the payment recorded in BTCPay.
///
/// Proofs are persisted to DB after step 3 and cleared after step 4,
/// enabling crash-safe retry of the melt without re-minting.
/// </summary>
/// <remarks>
/// <para><b>BTCPay invoice Settled vs CashuMelt <c>SettlementState</c>:</b>
/// The BTCPay invoice transitions to <c>InvoiceStatus.Settled</c> only after
/// <see cref="TryRecordPaymentInBtcPayAsync"/> runs successfully (after a successful melt).
/// Plugin row <c>SETTLED</c> means melt + BTCPay payment row + <c>ReceivedPayment</c> event were applied.
/// <c>MELT_COMPLETE</c> means melt succeeded but BTCPay accounting is retried on poll or <c>POST .../retry</c>.</para>
/// <para><b>Successful payment — grep-friendly log sequence (same quote + invoice):</b>
/// <c>cashumelt_mint_proof_ok</c> → <c>cashumelt_forward_ok</c> → <c>cashumelt_btcpay_recorded</c> → <c>cashumelt_settlement_complete</c>.</para>
/// </remarks>
public class CashuMeltPaymentService
{
    private readonly CashuMeltMintIssuance _issuance;
    private readonly CashuMeltAssetProtector _assets;
    private readonly CashuMeltMintClient _mintClient;
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentService _paymentService;
    private readonly EventAggregator _eventAggregator;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CashuMeltPaymentService> _logger;

    // Prevents concurrent polls from triggering simultaneous mint attempts for the same quote.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _quoteProcessingLocks = new();

    /// <summary>Per-quote backoff after mint HTTP 429/5xx so we do not hammer the mint every ~2s.</summary>
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset NextAllowedUtc)> _mintQuotePollBackoff = new();

    private static readonly JsonSerializerOptions ProofJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CashuMeltPaymentService(
        CashuMeltMintClient mintClient,
        CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        EventAggregator eventAggregator,
        PaymentMethodHandlerDictionary handlers,
        IHttpClientFactory httpClientFactory,
        ILogger<CashuMeltPaymentService> logger,
        CashuMeltMintIssuance issuance, CashuMeltAssetProtector assets)
    {
        _mintClient = mintClient;
        _issuance = issuance;
        _assets = assets;
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _eventAggregator = eventAggregator;
        _handlers = handlers;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────
    // Quote creation (called from CashuMeltPaymentMethodHandler)
    // ──────────────────────────────────────────────────────────────

    public async Task<(string? QuoteId, string? Bolt11, string? Error)> CreateQuoteAsync(
        string invoiceId,
        string storeId,
        long amountSats,
        string unit,
        CancellationToken ct = default)
    {
        var settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null)
            return (null, null, "CashuMelt not configured for this store");

        try
        {
            CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(settings);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, ex.Message);
        }

        await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct);
        if (storeLease is null) return (null, null, "CashuMelt settings update in progress");
        settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null) return (null, null, "CashuMelt not configured");
        if (amountSats <= CashuMeltFeePolicy.EstimateFeeBufferSat(amountSats))
            return (null, null, "Payment amount is too small to cover the Lightning payout reserve");
        if (unit != "sat") return (null, null, "Lightning settlement currently requires a sat-denominated mint");
        if (!await _mintClient.SupportsRestoreAsync(settings.MintUrl, ct))
            return (null, null, "Mint must support NUT-09 recovery before accepting payments");

        var quote = await _mintClient.CreateMintQuoteAsync(settings.MintUrl, amountSats, unit, ct);
        if (quote is null || string.IsNullOrEmpty(quote.Quote))
            return (null, null, "Mint did not return a valid quote");

        if (quote.Request is null) return (null, null, "Mint did not return an invoice");
        CashuMeltSettlementVerification.ValidateInvoice(quote.Request, amountSats);
        if (quote.Unit != unit || quote.Amount != amountSats) return (null, null, "Mint quote amount or unit mismatch");

        var req = new CashuMeltPaymentRequest
        {
            QuoteId        = Guid.NewGuid().ToString("N"),
            MintQuoteId = _assets.Protect(quote.Quote),
            MintUrl = settings.MintUrl,
            SettingsSnapshotJson = JsonSerializer.Serialize(settings),
            QuoteExpiresAt = quote.Expiry is > 0 ? DateTimeOffset.FromUnixTimeSeconds(quote.Expiry.Value) : null,
            InvoiceId      = invoiceId,
            StoreId        = storeId,
            AmountSats     = amountSats,
            Unit           = unit,
            Bolt11Invoice  = quote.Request,
            State          = quote.State,
            SettlementState = "PENDING",
            CreatedAt      = DateTimeOffset.UtcNow
        };

        await WithSchemaRetryAsync(async ctx =>
        {
            await ctx.CashuMeltPaymentRequests.AddAsync(req, ct);
            await ctx.SaveChangesAsync(ct);
        }, ct);

        _logger.LogInformation(
            "phase=mint_quote quote={QuoteId} invoice={InvoiceId} amountSat={AmountSat} unit={Unit}",
            req.QuoteId, invoiceId, amountSats, unit);

        return (req.QuoteId, quote.Request, null);
    }

    // ──────────────────────────────────────────────────────────────
    // Payment detection → mint → melt → record in BTCPay (poll endpoint)
    // ──────────────────────────────────────────────────────────────

    /// <returns>Paid, user-visible error (if any), optional seconds for checkout to backoff mint polls.</returns>
    public async Task<(bool Paid, string? Error, int? RetryAfterSeconds)> CheckAndRecordPaymentAsync(
        string quoteId, CancellationToken ct = default)
    {
        var sem = _quoteProcessingLocks.GetOrAdd(quoteId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
            return (false, null, null);

        try
        {
            await using var ctx = await CreateReadyContextAsync(ct);

            var req = await ctx.CashuMeltPaymentRequests.FirstOrDefaultAsync(r => r.QuoteId == quoteId, ct);
            if (req is null)
                return (false, "Quote not found", null);

            await using var quoteLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:quote:" + quoteId, false, ct);
            if (quoteLease is null) return (false, null, 3);
            await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + req.StoreId, true, ct);
            if (storeLease is null) return (false, null, 3);
            await ctx.Entry(req).ReloadAsync(ct);
            req.LastCheckedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(ct);

            if (req.SettlementState is "MANUAL_REVIEW" or "EXPIRED_UNPAID")
                return (false, req.SettlementError, null);

            if (req.SettlementState == "SETTLED")
                return (true, null, null);

            if (req.SettlementState == "FAILED")
                return (false, req.SettlementError, null);

            // Melt already succeeded; only BTCPay accounting was flaky — retry recording only.
            if (req.SettlementState == "MELT_COMPLETE")
            {
                var invForAccounting = await _invoiceRepository.GetInvoice(req.InvoiceId);
                if (invForAccounting is null)
                    return (false, "BTCPay invoice not found", null);

                var recorded = await TryRecordPaymentInBtcPayAsync(req, invForAccounting, ct);
                if (recorded)
                {
                    req.SettlementState = "SETTLED";
                    req.SettledAt = DateTimeOffset.UtcNow;
                    await ctx.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                        CashuMeltObservability.TagSettlementComplete,
                        CashuMeltObservability.PhaseBtcpay,
                        req.InvoiceId,
                        quoteId,
                        req.AmountSats);
                    return (true, null, null);
                }

                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagBtcpayRetry,
                    CashuMeltObservability.PhaseBtcpay,
                    req.InvoiceId,
                    quoteId,
                    req.AmountSats);
                return (false, null, 3);
            }

            var settings = req.SettingsSnapshotJson is null ? null : JsonSerializer.Deserialize<CashuMeltStoreSettings>(req.SettingsSnapshotJson);
            if (settings is null || req.MintUrl != settings.MintUrl)
                return await RequireManualReviewAsync(ctx, req, "Original mint and payout configuration must be confirmed for this legacy payment", ct);
            if (req.Unit != "sat") return await RequireManualReviewAsync(ctx, req, "USD mint settlement requires explicit currency conversion; payment preserved", ct);

            try
            {
                CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(settings);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }

            if (_mintQuotePollBackoff.TryGetValue(quoteId, out var backoff) && DateTimeOffset.UtcNow < backoff.NextAllowedUtc)
            {
                var wait = (int)Math.Ceiling((backoff.NextAllowedUtc - DateTimeOffset.UtcNow).TotalSeconds);
                return (false, null, Math.Max(1, wait));
            }

            var pollResult = await _mintClient.GetMintQuoteForPollAsync(settings.MintUrl, _assets.Unprotect(req.MintQuoteId ?? req.QuoteId), ct);
            if (pollResult.TransientFailure)
            {
                var suggested = pollResult.RetryAfterSeconds;
                var newBackoff = _mintQuotePollBackoff.AddOrUpdate(
                    quoteId,
                    _ => (1, DateTimeOffset.UtcNow.AddSeconds(ComputeBackoffSeconds(1, suggested))),
                    (_, prev) =>
                    {
                        var f = prev.Failures + 1;
                        return (f, DateTimeOffset.UtcNow.AddSeconds(ComputeBackoffSeconds(f, suggested)));
                    });
                var retryAfter = (int)Math.Ceiling((newBackoff.NextAllowedUtc - DateTimeOffset.UtcNow).TotalSeconds);
                return (false, null, Math.Max(1, retryAfter));
            }

            if (!pollResult.Success || pollResult.Quote is null)
            {
                _mintQuotePollBackoff.TryRemove(quoteId, out _);
                var hardErr = pollResult.ErrorMessage ?? "Failed to get quote status from mint";
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseMintPoll, hardErr, ct,
                    CashuMeltFailureReasons.MintPollError);
                return (false, hardErr, null);
            }

            _mintQuotePollBackoff.TryRemove(quoteId, out _);

            var mintQuote = pollResult.Quote;
            if (mintQuote.Quote != _assets.Unprotect(req.MintQuoteId ?? req.QuoteId) || mintQuote.Unit != req.Unit || mintQuote.Amount != req.AmountSats
                || (mintQuote.Request is not null && mintQuote.Request != req.Bolt11Invoice))
                return await RequireManualReviewAsync(ctx, req, "Mint quote does not match the original invoice", ct);
            if (!mintQuote.HasBeenPaid)
            {
                if (req.QuoteExpiresAt < DateTimeOffset.UtcNow && mintQuote.State == "UNPAID"
                    && string.IsNullOrEmpty(req.MintedProofsJson) && string.IsNullOrEmpty(req.MintIssuanceJson)
                    && string.IsNullOrEmpty(req.BlankOutputsJson) && req.MeltQuoteId is null)
                {
                    var originalInvoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
                    if (originalInvoice is not null && originalInvoice.MonitoringExpiration < DateTimeOffset.UtcNow)
                    {
                        req.SettlementState = "EXPIRED_UNPAID";
                        await ctx.SaveChangesAsync(ct);
                    }
                }
                return (false, null, null);
            }

            var invoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
            if (invoice is null)
                return (false, "BTCPay invoice not found", null);

            if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Invalid)
                return await RequireManualReviewAsync(ctx, req, "Invoice already settled or invalid: paid Cashu quote requires overpayment or refund review", ct);

            if (string.IsNullOrWhiteSpace(settings.LightningAddress))
            {
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    "No Lightning address configured for merchant payout.", ct,
                    CashuMeltFailureReasons.LightningAddressUnresolvable);
                return (false, req.SettlementError, null);
            }

            req.State = mintQuote.State;
            req.PaidAt ??= DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(ct);

            CashuMeltProof[] proofs;
            if (!string.IsNullOrEmpty(req.MintedProofsJson))
            {
                _logger.LogInformation(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagMeltRetry,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    quoteId,
                    req.AmountSats);
                proofs = JsonSerializer.Deserialize<CashuMeltProof[]>(_assets.Unprotect(req.MintedProofsJson), ProofJsonOptions)
                         ?? throw new InvalidOperationException("Stored proofs JSON is corrupt");
            }
            else
            {
                var (mintedProofs, mintError, mintReasonCode) = await MintProofsAsync(settings, req, mintQuote.HasBeenIssued ? "ISSUED" : "PAID", ctx, ct);
                if (mintError is not null)
                {
                    await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseMintProof, mintError, ct, mintReasonCode);
                    return (false, mintError, null);
                }
                proofs = mintedProofs!;
            }

            var (meltOk, meltError, transientMelt) = await MeltToMerchantAsync(settings, req, invoice, proofs, ctx, ct);
            if (!meltOk)
                return transientMelt ? (false, null, 3) : (false, meltError, null);

            var invoiceForPayment = await _invoiceRepository.GetInvoice(req.InvoiceId) ?? invoice;
            var recordedAfterMelt = await TryRecordPaymentInBtcPayAsync(req, invoiceForPayment, ct);
            if (!recordedAfterMelt)
            {
                req.SettlementState = "MELT_COMPLETE";
                await ctx.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagBtcpayRetry,
                    CashuMeltObservability.PhaseBtcpay,
                    req.InvoiceId,
                    quoteId,
                    req.AmountSats);
                return (false, null, 3);
            }

            req.SettlementState = "SETTLED";
            req.SettledAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(ct);

            _logger.LogInformation(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} unit={Unit}",
                CashuMeltObservability.TagSettlementComplete,
                CashuMeltObservability.PhaseBtcpay,
                req.InvoiceId,
                quoteId,
                req.AmountSats,
                req.Unit);

            return (true, null, null);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Retries settlement for a store quote: <c>PENDING</c>, <c>FAILED</c> (when minted proofs exist), or <c>MELT_COMPLETE</c> (BTCPay accounting only).
    /// Used by the Greenfield retry endpoint and the store CashuMelt settings UI.
    /// </summary>
    public async Task<CashuMeltRetryOutcome> RetrySettlementAsync(string storeId, string quoteId, CancellationToken ct = default)
    {
        await using var quoteLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:quote:" + quoteId, false, ct);
        if (quoteLease is null) return new CashuMeltRetryOutcome(CashuMeltRetryKind.Completed, false, null, 3);
        await using var ctx = _dbContextFactory.CreateContext();
        var r = await ctx.CashuMeltPaymentRequests
            .FirstOrDefaultAsync(x => x.QuoteId == quoteId && x.StoreId == storeId, ct);

        if (r is null)
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.NotFound);

        if (r.SettingsSnapshotJson is null)
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.CannotRetryMissingProofs, Error: "Confirm the original mint and payout address before retrying this legacy payment");

        if (r.SettlementState == "SETTLED")
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.AlreadySettled, Settled: true);

        if (r.SettlementState == "MELT_COMPLETE")
        {
            await quoteLease.DisposeAsync();
            var (paidMc, errMc, raMc) = await CheckAndRecordPaymentAsync(quoteId, ct);
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.Completed, paidMc, errMc, raMc);
        }

        if (string.IsNullOrEmpty(r.MintedProofsJson) && string.IsNullOrEmpty(r.MintIssuanceJson) && r.SettlementState == "FAILED")
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.CannotRetryMissingProofs);

        // Manual retry resets escalation state so background reconciliation can resume.
        r.SettlementState = "PENDING";
        r.SettlementError = null;
        r.NeedsManualReview = false;
        r.RetryCount = 0;
        r.FailureReasonCode = null;
        await ctx.SaveChangesAsync(ct);

        await quoteLease.DisposeAsync();
        var (paid, error, retryAfter) = await CheckAndRecordPaymentAsync(quoteId, ct);
        return new CashuMeltRetryOutcome(CashuMeltRetryKind.Completed, paid, error, retryAfter);
    }

    public async Task ConfirmOriginalSettingsAsync(string storeId, string quoteId, string mintUrl, string address, CancellationToken ct)
    {
        await using var quoteLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:quote:" + quoteId, false, ct)
            ?? throw new InvalidOperationException("Settlement is already running");
        await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct)
            ?? throw new InvalidOperationException("Store settings update is already running");
        await using var ctx = await CreateReadyContextAsync(ct);
        var request = await ctx.CashuMeltPaymentRequests.FirstOrDefaultAsync(r => r.StoreId == storeId && r.QuoteId == quoteId, ct)
            ?? throw new InvalidOperationException("Payment not found");
        if (request.SettingsSnapshotJson is not null) throw new InvalidOperationException("Original settings are already frozen");
        mintUrl = CashuMeltMintPolicy.NormalizeMintUrl(mintUrl);
        CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(new CashuMeltStoreSettings { MintUrl = mintUrl });
        var quote = await _mintClient.GetMintQuoteForPollAsync(mintUrl, request.QuoteId, ct);
        if (!quote.Success || quote.Quote is null || quote.Quote.Request != request.Bolt11Invoice || quote.Quote.Amount != request.AmountSats || quote.Quote.Unit != request.Unit)
            throw new InvalidOperationException("The supplied mint does not identify the original funding invoice");
        var resolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
        await CashuMeltSettingsValidation.ValidateLightningAddressResolvableAsync(address, resolver, ct);
        var snapshot = new CashuMeltStoreSettings { StoreId = storeId, MintUrl = mintUrl, LightningAddress = address.Trim(), Unit = request.Unit, Enabled = true };
        request.MintUrl = mintUrl;
        request.MintQuoteId = _assets.Protect(request.QuoteId);
        request.SettingsSnapshotJson = JsonSerializer.Serialize(snapshot);
        request.QuoteExpiresAt = quote.Quote.Expiry is > 0 ? DateTimeOffset.FromUnixTimeSeconds(quote.Quote.Expiry.Value) : null;
        request.SettlementState = "PENDING";
        request.NeedsManualReview = false;
        request.SettlementError = null;
        await ctx.SaveChangesAsync(ct);
    }

    // Merchant-only recovery. Exporting does not resolve custody: safety remains blocked
    // until the mint confirms that the merchant's wallet has consumed every proof.
    public async Task<string[]> ExportChangeAsync(string storeId, CancellationToken ct)
    {
        await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:sweep", false, ct)
            ?? throw new InvalidOperationException("Change sweep is in progress");
        await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct)
            ?? throw new InvalidOperationException("Settings update is in progress");
        await using var ctx = await CreateReadyContextAsync(ct);
        var rows = await ctx.CashuMeltChangeProofs.Where(p => p.StoreId == storeId && p.Unit == "sat" && (p.State == "AVAILABLE" || p.State == "EXPORTED")).ToListAsync(ct);
        var tokens = rows.GroupBy(p => p.MintUrl).Select(group =>
        {
            var proofs = group.Select(p => new CashuMeltProof(p.Amount, p.KeysetId, _assets.Unprotect(p.Secret), p.C)).ToArray();
            var json = JsonSerializer.Serialize(new { token = new[] { new { mint = group.Key, proofs } }, unit = "sat" }, ProofJsonOptions);
            return "cashuA" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }).ToArray();
        foreach (var row in rows) row.State = "EXPORTED";
        await ctx.SaveChangesAsync(ct);
        return tokens;
    }

    public async Task ConfirmChangeRecoveredAsync(string storeId, CancellationToken ct)
    {
        await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:sweep", false, ct)
            ?? throw new InvalidOperationException("Change sweep is in progress");
        await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct)
            ?? throw new InvalidOperationException("Settings update is in progress");
        await using var ctx = await CreateReadyContextAsync(ct);
        var rows = await ctx.CashuMeltChangeProofs.Where(p => p.StoreId == storeId && p.State == "EXPORTED").ToListAsync(ct);
        foreach (var group in rows.GroupBy(p => p.MintUrl))
        {
            var proofs = group.Select(p => new CashuMeltProof(p.Amount, p.KeysetId, _assets.Unprotect(p.Secret), p.C)).ToArray();
            if (await TryGetAggregateProofStateAsync(group.Key, proofs, ct) != "SPENT")
                throw new InvalidOperationException("Exported change has not been fully consumed by the merchant wallet");
        }
        foreach (var row in rows) { row.State = "RECOVERED"; row.Secret = "consumed"; }
        await ctx.SaveChangesAsync(ct);
    }

    private async Task<(bool Paid, string? Error, int? RetryAfterSeconds)> RequireManualReviewAsync(
        CashuMeltDbContext ctx, CashuMeltPaymentRequest req, string error, CancellationToken ct)
    {
        req.SettlementState = "MANUAL_REVIEW";
        req.SettlementError = error;
        req.NeedsManualReview = true;
        req.FailureReasonCode = "settlement_evidence_required";
        await ctx.SaveChangesAsync(ct);
        return (false, error, null);
    }

    private async Task MarkFailedAsync(
        CashuMeltDbContext ctx,
        CashuMeltPaymentRequest req,
        string phase,
        string error,
        CancellationToken ct,
        string? reasonCode = null)
    {
        if (req.SettlementState is "SETTLED" or "FAILED")
            return;
        var e = error.Length > 500 ? error[..500] : error;
        req.SettlementState = "FAILED";
        req.SettlementError = e;
        req.FailureReasonCode = reasonCode;
        await ctx.SaveChangesAsync(ct);
        _logger.LogWarning(
            "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} reasonCode={ReasonCode} msg={Detail}",
            CashuMeltObservability.TagSettlementFailed,
            phase,
            req.InvoiceId,
            req.QuoteId,
            req.AmountSats,
            reasonCode ?? "unclassified",
            e);
    }

    private static int ComputeBackoffSeconds(int consecutiveFailures, int? retryAfterFromMint)
    {
        if (retryAfterFromMint is > 0 and <= 300)
            return retryAfterFromMint.Value;
        var exp = (int)Math.Pow(2, Math.Min(consecutiveFailures, 6));
        return Math.Clamp(Math.Max(exp, 2), 2, 120);
    }

    // ──────────────────────────────────────────────────────────────
    // Step 2: Mint proofs (NUT-05)
    // ──────────────────────────────────────────────────────────────

    private async Task<(CashuMeltProof[]? Proofs, string? Error, string? ReasonCode)> MintProofsAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        string mintQuoteState,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        try
        {
            var proofs = await _issuance.IssueAsync(settings.MintUrl, _assets.Unprotect(req.MintQuoteId ?? req.QuoteId), req.Unit, req.AmountSats,
                mintQuoteState == "ISSUED", req.MintIssuanceJson,
                async intent => { req.MintIssuanceJson = intent; await ctx.SaveChangesAsync(ct); },
                async proofs =>
                {
                    req.MintedProofsJson = _assets.Protect(JsonSerializer.Serialize(proofs, ProofJsonOptions));
                    req.State = mintQuoteState;
                    req.PaidAt ??= DateTimeOffset.UtcNow;
                    await ctx.SaveChangesAsync(ct);
                }, ct);
            return (proofs, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cashu issuance deferred for {QuoteId}: {ErrorType}", req.QuoteId, ex.GetType().Name);
            return (null, "Could not issue or restore tokens; recovery intent preserved", CashuMeltFailureReasons.MintProofFailed);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Step 3: Melt proofs to merchant Lightning address (NUT-14)
    // ──────────────────────────────────────────────────────────────

    /// <summary>transientMelt: caller should poll again without surfacing a hard error to the payer.</summary>
    internal async Task<(bool Ok, string? Error, bool TransientMelt)> MeltToMerchantAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        InvoiceEntity invoice,
        CashuMeltProof[] proofs,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        var totalMintedSat = proofs.Sum(p => p.Amount);

        // Reconcile a previously created melt quote before paying again: if the mint already
        // paid it (crash or misread melt response after a prior melt), the stored proofs are
        // spent and a second melt must not be attempted.
        if (!string.IsNullOrEmpty(req.MeltQuoteId))
        {
            MeltQuoteResponse? priorQuote;

            try
            {
                priorQuote = await _mintClient.GetMeltQuoteAsync(settings.MintUrl, req.MeltQuoteId, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                priorQuote = null; // Unknown outcome: retain the original quote and invoice.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} meltQuote={MeltQuoteId} msg=melt_quote_state_check_failed",
                    CashuMeltObservability.TagMeltRetry,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    req.MeltQuoteId);

                return (false, null, true);
            }

            switch (ClassifyPriorMeltQuote(priorQuote))
            {
                case PriorMeltQuoteDecision.CompleteSettlement:
                    _logger.LogInformation(
                        "Prior melt quote {MeltQuoteId} already PAID for invoice {InvoiceId} - completing settlement without a second melt",
                        req.MeltQuoteId, req.InvoiceId);
                    if (!CashuMeltSettlementVerification.IsPaidQuote(priorQuote, req.MeltQuoteId!, req.ForwardBolt11))
                    {
                        await RequireManualReviewAsync(ctx, req, "Invalid confirmation for the original payout", ct);
                        return (false, req.SettlementError, false);
                    }
                    if (!await TryStoreChangeProofsAsync(settings, req, priorQuote!.Change, ctx, ct)) return (false, null, true);
                    return await CompleteMeltAsync(req, ctx, priorQuote.PaymentPreimage, priorQuote.Amount, ct);
                case PriorMeltQuoteDecision.WaitPending:
                    return (false, null, true);
            }

            // Retry the exact durable intent; never replace a payout after an ambiguous POST.
            if (priorQuote is null) return (false, null, true);
            if (priorQuote.Quote != req.MeltQuoteId || priorQuote.State != "UNPAID"
                || priorQuote.Amount != req.ForwardAmountSats
                || (priorQuote.Request is not null && priorQuote.Request != req.ForwardBolt11))
            {
                await RequireManualReviewAsync(ctx, req, "Original payout requires destination-bound settlement evidence", ct);
                return (false, req.SettlementError, false);
            }
            try { CashuMeltSettlementVerification.ValidateInvoice(req.ForwardBolt11!, priorQuote.Amount); }
            catch
            {
                await RequireManualReviewAsync(ctx, req, "Original payout invoice expired; preserve its identity for recovery", ct);
                return (false, req.SettlementError, false);
            }
            if (await TryGetAggregateProofStateAsync(settings.MintUrl, proofs, ct) != "UNSPENT")
                return (false, null, true);
            BlindedMessage[]? originalOutputs = null;
            if (req.BlankOutputsJson is not null)
            {
                var blanks = JsonSerializer.Deserialize<BlankOutputBlinding[]>(_assets.Unprotect(req.BlankOutputsJson), ProofJsonOptions);
                if (blanks is null || blanks.Any(b => b.B_ is null))
                {
                    await RequireManualReviewAsync(ctx, req, "Legacy change intent needs manual recovery", ct);
                    return (false, req.SettlementError, false);
                }
                originalOutputs = blanks.Select(b => new BlindedMessage(1, b.KeysetId, b.B_!)).ToArray();
            }
            try { await _mintClient.MeltTokensAsync(settings.MintUrl, req.MeltQuoteId!, proofs, originalOutputs, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CashuMeltMintProtocolException)
            { /* Outcome is unknown; reconcile the same quote on the next pass. */ }
            return (false, null, true);
        }

        // NUT-02: keyset input fee for spending the minted proofs - without it a
        // fee-charging mint rejects the melt for insufficient inputs.
        var maxPpk = await TryGetMaxInputFeePpkAsync(settings.MintUrl, proofs, ct);
        if (maxPpk is null)
        {
            _logger.LogWarning(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} msg=keysets_info_unavailable",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId);
            return (false, null, true);
        }
        var keysetInputFeeSat = CashuMeltFeePolicy.KeysetInputFeeSat(proofs.Length, maxPpk.Value);
        req.InputFeeSats = keysetInputFeeSat;

        var spendableSat = totalMintedSat - keysetInputFeeSat;
        long feeBuffer = CashuMeltFeePolicy.EstimateFeeBufferSat(spendableSat);
        long forwardSat = spendableSat - feeBuffer;

        if (forwardSat <= 0)
        {
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                $"Amount ({totalMintedSat} sat) too small to cover routing fee buffer ({feeBuffer} sat) and keyset input fee ({keysetInputFeeSat} sat)",
                ct, CashuMeltFailureReasons.AmountTooSmall);
            return (false, req.SettlementError, false);
        }

        // The mint reveals its actual Lightning fee reserve only in the melt quote, so the
        // estimated buffer can undershoot. When it does, shrink the forwarded amount to
        // totalMinted - actual reserve, fetch a fresh invoice and re-quote.
        MeltQuoteResponse? meltQuote = null;
        const int maxFeeAdjustAttempts = 3;
        var lnResolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
        for (var attempt = 1; attempt <= maxFeeAdjustAttempts; attempt++)
        {
            string bolt11;
            try
            {
                var (resolvedBolt11, effectiveSat) = await lnResolver.ResolveInvoiceAsync(
                    settings.LightningAddress!.Trim(), forwardSat, spendableSat, ct);
                bolt11 = resolvedBolt11;

                if (effectiveSat != forwardSat)
                    _logger.LogInformation(
                        "Forward amount adjusted from {Desired} to {Effective} sat (LNURL limits) for invoice {InvoiceId} quote {QuoteId}",
                        forwardSat, effectiveSat, req.InvoiceId, req.QuoteId);

                forwardSat = effectiveSat;
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    $"Could not resolve Lightning address: {ex.Message}", ct,
                    CashuMeltFailureReasons.LightningAddressUnresolvable);
                _logger.LogError(ex,
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagSettlementFailed,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    totalMintedSat);
                return (false, req.SettlementError, false);
            }

            if (req.ForwardBolt11 != bolt11)
            {
                req.ForwardBolt11 = bolt11;
                await ctx.SaveChangesAsync(ct);
            }

            try
            {
                meltQuote = await _mintClient.RequestMeltQuoteAsync(settings.MintUrl, bolt11, req.Unit, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagMeltRetry,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    totalMintedSat);
                return (false, null, true);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagMeltRetry,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    totalMintedSat);
                return (false, null, true);
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    $"Melt quote request failed: {ex.Message}", ct,
                    CashuMeltFailureReasons.MeltQuoteFailed);
                _logger.LogError(ex,
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                    CashuMeltObservability.TagSettlementFailed,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    totalMintedSat);
                return (false, req.SettlementError, false);
            }

            if (meltQuote is not null && (meltQuote.Amount != forwardSat || (meltQuote.Request is not null && meltQuote.Request != bolt11)))
            {
                await RequireManualReviewAsync(ctx, req, "Melt quote does not match the destination invoice", ct);
                return (false, req.SettlementError, false);
            }
            if (meltQuote is null)
            {
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    "Mint returned no melt quote", ct,
                    CashuMeltFailureReasons.MeltQuoteFailed);
                return (false, req.SettlementError, false);
            }

            var feeCapErr = CashuMeltFeePolicy.ValidateMeltFeeReserve(
                totalMintedSat,
                meltQuote.FeeReserve,
                settings.MaxMeltFeeReserveSats,
                settings.MaxMeltFeeReservePercentOfMinted);
            if (feeCapErr is not null)
            {
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    feeCapErr, ct, CashuMeltFailureReasons.FeeTooHigh);
                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} feeReserve={FeeReserve} msg=fee_cap",
                    CashuMeltObservability.TagSettlementFailed,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    meltQuote.FeeReserve);
                return (false, feeCapErr, false);
            }

            long totalNeeded = meltQuote.Amount + meltQuote.FeeReserve;
            if (totalNeeded <= spendableSat)
                break;

            var reducedForwardSat = CashuMeltFeePolicy.ReducedForwardSat(spendableSat, meltQuote.FeeReserve, forwardSat);
            if (attempt >= maxFeeAdjustAttempts || reducedForwardSat is null)
            {
                var feeErr =
                    $"Lightning routing fee reserve ({meltQuote.FeeReserve} sat) is too high for this payment ({totalMintedSat} sat minted). " +
                    "Try a slightly larger amount or adjust the merchant Lightning address limits.";
                await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                    feeErr, ct, CashuMeltFailureReasons.FeeTooHigh);
                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} feeReserve={FeeReserve} msg=fee_reserve_exceeds_minted",
                    CashuMeltObservability.TagSettlementFailed,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    meltQuote.FeeReserve);
                return (false, req.SettlementError, false);
            }

            _logger.LogInformation(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} feeReserve={FeeReserve} forwardSat={ForwardSat} reducedTo={ReducedForwardSat} attempt={Attempt} msg=fee_reserve_adjust",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                meltQuote.FeeReserve,
                forwardSat,
                reducedForwardSat.Value,
                attempt);
            forwardSat = reducedForwardSat.Value;
        }

        if (meltQuote is null)
        {
            // Unreachable: the final loop iteration either breaks or returns. Kept for safety.
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                "Melt quote fee adjustment did not converge", ct, CashuMeltFailureReasons.FeeTooHigh);
            return (false, req.SettlementError, false);
        }

        // NUT-08: send blank outputs so the mint returns unused fee reserve as change.
        // Blinding data is persisted in the same save as the quote id, so change can be
        // recovered via GET melt quote even after a crash or lost response.
        BlindedMessage[]? blankOutputs = null;
        if (meltQuote.FeeReserve > 0)
        {
            try
            {
                var (outputs, blinding) = CreateBlankOutputs(meltQuote.FeeReserve, proofs);
                blankOutputs = outputs;
                req.BlankOutputsJson = _assets.Protect(JsonSerializer.Serialize(blinding, ProofJsonOptions));
            }
            catch (Exception ex)
            {
                // Change is a bonus - never block the melt because of it.
                _logger.LogWarning(ex,
                    "Could not create NUT-08 blank outputs for invoice {InvoiceId} quote {QuoteId}",
                    req.InvoiceId, req.QuoteId);
                blankOutputs = null;
                req.BlankOutputsJson = null;
            }
        }

        req.MeltQuoteId = meltQuote.Quote;
        req.ForwardAmountSats = forwardSat;
        req.MeltFeeReserveSats = meltQuote.FeeReserve;
        await ctx.SaveChangesAsync(ct);

        MeltTokensResponse? meltResp;
        try
        {
            meltResp = await _mintClient.MeltTokensAsync(settings.MintUrl, meltQuote.Quote, proofs, blankOutputs, ct);
        }
        catch (CashuMeltMintProtocolException ex) when (ex.MintErrorCode == CashuMeltMintProtocolException.TokenAlreadySpent)
        {
            // Preserve the exact quote and proofs; a later GET can confirm the payout.
            return (false, null, true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                totalMintedSat);
            return (false, null, true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                totalMintedSat);
            return (false, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat}",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                totalMintedSat);
            return (false, null, true);
        }

        if (meltResp is null)
        {
            // Unknown outcome - the melt POST may have succeeded at the mint. MeltQuoteId is
            // stored, so the next poll reconciles the quote state instead of failing hard.
            _logger.LogWarning(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} msg=empty_melt_response",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId);
            return (false, null, true);
        }

        if (MeltStatePending(meltResp.State))
        {
            // Lightning payment still in flight - poll again; MeltQuoteId is stored, so the
            // next pass reconciles the quote state instead of melting a second time.
            _logger.LogInformation(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} msg=melt_pending",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId);
            return (false, null, true);
        }

        if (!MeltStatePaid(meltResp.State))
        {
            const string err = "Mint did not confirm Lightning payment";
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward, err, ct,
                CashuMeltFailureReasons.MeltFailed);
            return (false, err, false);
        }

        if (!await TryStoreChangeProofsAsync(settings, req, meltResp.Change, ctx, ct)) return (false, null, true);
        return await CompleteMeltAsync(req, ctx, meltResp.PaymentPreimage ?? meltResp.Proof, forwardSat, ct);
    }

    /// <summary>Marks the melt as done: stores the preimage, clears spent proofs.</summary>
    private async Task<(bool Ok, string? Error, bool TransientMelt)> CompleteMeltAsync(
        CashuMeltPaymentRequest req,
        CashuMeltDbContext ctx,
        string? preimage,
        long? forwardSat,
        CancellationToken ct)
    {
        if (!CashuMeltSettlementVerification.ValidPreimage(preimage, req.ForwardBolt11))
        {
            await RequireManualReviewAsync(ctx, req, "Payout preimage does not match the original invoice", ct);
            return (false, req.SettlementError, false);
        }
        req.SettlementReference = preimage;
        req.MintedProofsJson = null;
        req.MintIssuanceJson = null;
        // Same save as the proof clear: a crash before BTCPay accounting must not leave a
        // PENDING row without proofs (the next poll would try to re-mint against the quote).
        req.SettlementState = "MELT_COMPLETE";
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} forwardSat={ForwardSat} preimage={HasPreimage}",
            CashuMeltObservability.TagForwardOk,
            CashuMeltObservability.PhaseForward,
            req.InvoiceId,
            req.QuoteId,
            forwardSat,
            !string.IsNullOrEmpty(preimage));

        return (true, null, false);
    }

    /// <summary>Outcome of reconciling a previously stored melt quote against the mint.</summary>
    public enum PriorMeltQuoteDecision
    {
        /// <summary>Quote unknown, UNPAID, or expired - proofs are unspent, melt again.</summary>
        FreshMelt,
        /// <summary>Quote PAID - the merchant already received the payment; do not melt again.</summary>
        CompleteSettlement,
        /// <summary>Quote PENDING - Lightning payment in flight; poll again later.</summary>
        WaitPending
    }

    /// <summary>Pure decision for a reconciled prior melt quote (null = mint no longer knows it).</summary>
    public static PriorMeltQuoteDecision ClassifyPriorMeltQuote(MeltQuoteResponse? priorQuote)
    {
        if (priorQuote is null)
            return PriorMeltQuoteDecision.FreshMelt;
        if (MeltStatePaid(priorQuote.State))
            return PriorMeltQuoteDecision.CompleteSettlement;
        if (MeltStatePending(priorQuote.State))
            return PriorMeltQuoteDecision.WaitPending;
        return PriorMeltQuoteDecision.FreshMelt;
    }

    private static bool MeltStatePaid(string? state) =>
        string.Equals(state, "PAID", StringComparison.OrdinalIgnoreCase);

    private static bool MeltStatePending(string? state) =>
        string.Equals(state, "PENDING", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blinding data for one NUT-08 blank output, persisted until change is stored.</summary>
    public sealed record BlankOutputBlinding(string Secret, string R, string KeysetId, string? B_ = null);

    /// <summary>
    /// NUT-08: blank outputs (amount 1, ignored by the mint) with fresh random secrets,
    /// using the keyset the proofs were minted with. Blinding data is returned for
    /// persistence so change signatures can be unblinded later.
    /// </summary>
    private static (BlindedMessage[] Outputs, BlankOutputBlinding[] Blinding) CreateBlankOutputs(
        long feeReserve, CashuMeltProof[] proofs)
    {
        if (proofs.Length == 0)
            throw new InvalidOperationException("Cannot create blank outputs without input proofs (keyset unknown).");

        var count = CashuMeltFeePolicy.BlankOutputCount(feeReserve);
        var keysetId = proofs[0].Id;
        var outputs = new BlindedMessage[count];
        var blinding = new BlankOutputBlinding[count];
        for (var i = 0; i < count; i++)
        {
            var secretHex = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var (bHex, r) = CashuMeltCrypto.CreateBlindedMessage(System.Text.Encoding.UTF8.GetBytes(secretHex));
            outputs[i] = new BlindedMessage(1, keysetId, bHex);
            blinding[i] = new BlankOutputBlinding(secretHex, Convert.ToHexString(r).ToLowerInvariant(), keysetId, bHex);
        }
        return (outputs, blinding);
    }

    /// <summary>
    /// Unblinds NUT-08 change signatures using the persisted blank-output blinding data and
    /// stores them as change proofs for the sweep. A failure retains the
    /// blinding data and blocks completion until every change signature is recovered.
    /// </summary>
    private async Task<bool> TryStoreChangeProofsAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        BlindSignature[]? change,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.BlankOutputsJson))
            return true;

        if (change is null || change.Length == 0)
        {
            req.BlankOutputsJson = null; // mint kept the whole reserve - nothing to recover
            return true;                      // caller's save persists the clear
        }

        var originalBlankOutputsJson = req.BlankOutputsJson;
        List<CashuMeltChangeProof>? rows = null;
        try
        {
            var blinding = JsonSerializer.Deserialize<BlankOutputBlinding[]>(_assets.Unprotect(req.BlankOutputsJson), ProofJsonOptions);
            if (blinding is null || blinding.Length == 0)
            {
                throw new InvalidOperationException("Missing change blinding data");
            }

            var keysResp = await GetChangeKeysAsync(settings.MintUrl, change, ct);
            rows = UnblindChangeToRows(
                req.StoreId, CashuMeltMintPolicy.NormalizeMintUrl(settings.MintUrl), req.Unit,
                req.QuoteId, change, blinding, keysResp);
            foreach (var row in rows)
            {
                if (!await ctx.CashuMeltChangeProofs.AnyAsync(p => p.MintUrl == row.MintUrl && p.KeysetId == row.KeysetId && p.C == row.C, ct))
                {
                    row.Secret = _assets.Protect(row.Secret);
                    ctx.CashuMeltChangeProofs.Add(row);
                }
            }

            req.BlankOutputsJson = null;
            await ctx.SaveChangesAsync(ct);

            if (rows.Count > 0)
                _logger.LogInformation(
                    "{Event} invoice={InvoiceId} quote={QuoteId} changeSat={ChangeSat} proofCount={ProofCount}",
                    CashuMeltObservability.TagChangeStored,
                    req.InvoiceId,
                    req.QuoteId,
                    rows.Sum(r => r.Amount),
                    rows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to store NUT-08 change for invoice {InvoiceId} quote {QuoteId}; blinding data retained for manual recovery",
                req.InvoiceId, req.QuoteId);
            // Keep the blinding data recoverable and detach only the rows staged here, so a
            // failed insert (e.g. duplicate secret) cannot poison the settlement save that
            // follows or discard unrelated tracked entities.
            req.BlankOutputsJson = originalBlankOutputsJson;
            if (rows is not null)
                foreach (var row in rows)
                    ctx.Entry(row).State = EntityState.Detached;
            return false;
        }
        return true;
    }

    private async Task<MintKeysResponse> GetChangeKeysAsync(string mintUrl, BlindSignature[] change, CancellationToken ct)
    {
        var keys = (await _mintClient.GetKeysAsync(mintUrl, ct))?.Keysets?.ToList() ?? new List<MintKeyset>();
        foreach (var id in change.Select(s => s.Id).Distinct())
        {
            if (keys.Any(k => k.Id == id)) continue;
            var historical = await _mintClient.GetKeysetKeysAsync(mintUrl, id, ct);
            keys.AddRange(historical?.Keysets?.Where(k => k.Id == id) ?? Array.Empty<MintKeyset>());
        }
        return new MintKeysResponse(keys.ToArray());
    }

    /// <summary>Unblinds NUT-08 change signatures into change-proof rows (invalid outputs fail closed).</summary>
    private List<CashuMeltChangeProof> UnblindChangeToRows(
        string storeId,
        string normalizedMintUrl,
        string unit,
        string? sourceQuoteId,
        BlindSignature[] change,
        BlankOutputBlinding[] blinding,
        MintKeysResponse? keysResp)
    {
        var rows = new List<CashuMeltChangeProof>(change.Length);
        if (change.Length > blinding.Length) throw new InvalidOperationException("Too many change signatures");
        for (var i = 0; i < change.Length; i++)
        {
            var sig = change[i];
            if (sig.Amount <= 0 || sig.Id != blinding[i].KeysetId)
                throw new InvalidOperationException("Invalid change signature amount or keyset");

            var keyset = keysResp?.Keysets?.FirstOrDefault(k => k.Id == sig.Id);
            if (keyset is null || !keyset.Keys.TryGetValue(sig.Amount.ToString(), out var mintKeyHex))
            {
                throw new InvalidOperationException("Original mint key is unavailable; change retained for recovery");
            }

            var cHex = CashuMeltCrypto.UnblindSignature(sig.C_, mintKeyHex, Convert.FromHexString(blinding[i].R));
            rows.Add(new CashuMeltChangeProof
            {
                StoreId = storeId,
                MintUrl = normalizedMintUrl,
                Unit = unit,
                Amount = sig.Amount,
                KeysetId = sig.Id,
                Secret = blinding[i].Secret,
                C = cHex,
                State = "AVAILABLE",
                SourceQuoteId = sourceQuoteId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        return rows;
    }

    // ──────────────────────────────────────────────────────────────
    // NUT-08 change sweep
    // ──────────────────────────────────────────────────────────────

    private const long MinChangeSweepSat = 100;

    /// <summary>
    /// Melts accumulated NUT-08 change proofs to the merchant Lightning address once a
    /// store's available change reaches <see cref="MinChangeSweepSat"/> sat. Rows left in
    /// SWEEPING by a crash are first reconciled via NUT-07 checkstate. Only sat-unit change
    /// is swept (LNURL amounts are sat-denominated).
    /// </summary>
    public async Task SweepAvailableChangeAsync(CancellationToken ct = default)
    {
        await using var sweepLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:sweep", false, ct);
        if (sweepLease is null) return;
        await using var ctx = await CreateReadyContextAsync(ct);

        await RecoverStuckSweepsAsync(ctx, ct);

        var groups = await ctx.CashuMeltChangeProofs.AsNoTracking()
            .Where(p => p.State == "AVAILABLE" && p.Unit == "sat")
            .GroupBy(p => new { p.StoreId, p.MintUrl })
            .Select(g => new { g.Key.StoreId, g.Key.MintUrl, Total = g.Sum(x => x.Amount) })
            .Where(g => g.Total >= MinChangeSweepSat)
            .ToListAsync(ct);

        foreach (var g in groups)
        {
            try
            {
                await SweepStoreChangeAsync(g.StoreId, g.MintUrl, ctx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NUT-08 change sweep failed for store {StoreId}", g.StoreId);
            }
        }
    }

    /// <summary>
    /// SWEEPING rows are recovered under the PostgreSQL sweep lease. Confirm the
    /// exact original melt quote before completing; resend only the same unpaid intent.
    /// </summary>
    private async Task RecoverStuckSweepsAsync(CashuMeltDbContext ctx, CancellationToken ct)
    {
        var stuck = await ctx.CashuMeltChangeProofs
            .Where(p => p.State == "SWEEPING")
            .OrderBy(p => p.Id)
            .Take(500)
            .ToListAsync(ct);
        if (stuck.Count == 0)
            return;

        foreach (var group in stuck.GroupBy(p => new { p.StoreId, p.MintUrl, p.SweepReference }))
        {
            await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + group.Key.StoreId, true, ct);
            if (lease is null) continue;
            var rows = group.ToList();
            if (rows[0].SweepIntentJson is null || group.Key.SweepReference is null)
            {
                foreach (var row in rows) row.State = "MANUAL_REVIEW";
                await ctx.SaveChangesAsync(ct);
                continue;
            }
            try
            {
                var intent = JsonSerializer.Deserialize<SweepIntent>(_assets.Unprotect(rows[0].SweepIntentJson!));
                if (intent is null || intent.QuoteId != group.Key.SweepReference) continue;
                var quote = await _mintClient.GetMeltQuoteAsync(group.Key.MintUrl, intent.QuoteId, ct);
                if (CashuMeltSettlementVerification.IsPaidQuote(quote, intent.QuoteId, intent.Bolt11))
                    await CompleteSweepAsync(ctx, rows, intent, quote!, ct);
                else if (quote?.State == "UNPAID" && quote.Quote == intent.QuoteId && (quote.Request is null || quote.Request == intent.Bolt11))
                {
                    try { CashuMeltSettlementVerification.ValidateInvoice(intent.Bolt11, quote.Amount); }
                    catch
                    {
                        foreach (var row in rows) row.State = "MANUAL_REVIEW";
                        await ctx.SaveChangesAsync(ct);
                        continue;
                    }
                    var proofs = rows.Select(r => new CashuMeltProof(r.Amount, r.KeysetId, _assets.Unprotect(r.Secret), r.C)).ToArray();
                    if (intent.Blinding?.Any(b => b.B_ is null) == true) continue;
                    if (await TryGetAggregateProofStateAsync(group.Key.MintUrl, proofs, ct) == "UNSPENT")
                    {
                        var outputs = intent.Blinding?.Select(b => new BlindedMessage(1, b.KeysetId, b.B_!)).ToArray();
                        await _mintClient.MeltTokensAsync(group.Key.MintUrl, intent.QuoteId, proofs, outputs, ct);
                        // Reconcile this same quote next time, even if the POST response is lost.
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning("Change recovery deferred: {ErrorType}", ex.GetType().Name); }
        }
    }

    private sealed record SweepIntent(string QuoteId, string Bolt11, BlankOutputBlinding[]? Blinding);

    private async Task CompleteSweepAsync(CashuMeltDbContext ctx, List<CashuMeltChangeProof> rows,
        SweepIntent intent, MeltQuoteResponse quote, CancellationToken ct)
    {
        if (!CashuMeltSettlementVerification.IsPaidQuote(quote, intent.QuoteId, intent.Bolt11)) return;
        if (quote.Change is { Length: > 0 })
        {
            if (intent.Blinding is null) throw new InvalidOperationException("Missing change recovery intent");
            var keys = await GetChangeKeysAsync(rows[0].MintUrl, quote.Change, ct);
            var change = UnblindChangeToRows(rows[0].StoreId, rows[0].MintUrl, "sat", null, quote.Change, intent.Blinding, keys);
            foreach (var proof in change)
            {
                if (!await ctx.CashuMeltChangeProofs.AnyAsync(p => p.MintUrl == proof.MintUrl && p.KeysetId == proof.KeysetId && p.C == proof.C, ct))
                {
                    proof.Secret = _assets.Protect(proof.Secret);
                    ctx.CashuMeltChangeProofs.Add(proof);
                }
            }
        }
        foreach (var row in rows)
        {
            row.State = "SWEPT";
            row.SweptAt = DateTimeOffset.UtcNow;
            row.SweepIntentJson = null;
            // Keep the exact melt identity instead of overwriting it with a preimage.
        }
        await ctx.SaveChangesAsync(ct);
    }

    private async Task SweepStoreChangeAsync(
        string storeId, string mintUrl, CashuMeltDbContext ctx, CancellationToken ct)
    {
        await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct);
        if (lease is null) return;
        var settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.LightningAddress))
            return;
        if (CashuMeltMintPolicy.NormalizeMintUrl(settings.MintUrl) != mintUrl)
            return; // store switched mints - keep the proofs until reconfigured back

        var candidates = await ctx.CashuMeltChangeProofs
            .Where(p => p.StoreId == storeId && p.MintUrl == mintUrl && p.State == "AVAILABLE" && p.Unit == "sat")
            .OrderBy(p => p.Id)
            .Take(200)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return;

        // One keyset per sweep: NUT-08 blank outputs are pinned to a single keyset, and a
        // batch mixing rotated keysets would make them invalid. Sweep the largest keyset
        // group now; the rest goes in later ticks.
        var rows = candidates
            .GroupBy(p => p.KeysetId)
            .OrderByDescending(g => g.Sum(x => x.Amount))
            .First()
            .ToList();
        var totalSat = rows.Sum(r => r.Amount);
        if (totalSat < MinChangeSweepSat)
            return;

        var proofs = rows.Select(r => new CashuMeltProof(r.Amount, r.KeysetId, _assets.Unprotect(r.Secret), r.C)).ToArray();

        var maxPpk = await TryGetMaxInputFeePpkAsync(mintUrl, proofs, ct);
        if (maxPpk is null)
            return; // keyset metadata unavailable - try again next tick
        var spendableSat = totalSat - CashuMeltFeePolicy.KeysetInputFeeSat(proofs.Length, maxPpk.Value);

        var forwardSat = spendableSat - CashuMeltFeePolicy.EstimateFeeBufferSat(spendableSat);
        if (forwardSat <= 0)
            return;

        var lnResolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
        MeltQuoteResponse? quote = null;
        string? payoutInvoice = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (bolt11, effectiveSat) = await lnResolver.ResolveInvoiceAsync(
                settings.LightningAddress!.Trim(), forwardSat, spendableSat, ct);
            forwardSat = effectiveSat;
            payoutInvoice = bolt11;

            quote = await _mintClient.RequestMeltQuoteAsync(mintUrl, bolt11, "sat", ct);
            if (quote is null || quote.Amount != forwardSat || (quote.Request is not null && quote.Request != bolt11))
                return;
            var feeError = CashuMeltFeePolicy.ValidateMeltFeeReserve(totalSat, quote.FeeReserve, settings.MaxMeltFeeReserveSats, settings.MaxMeltFeeReservePercentOfMinted);
            if (feeError is not null) return;
            if (quote.Amount + quote.FeeReserve <= spendableSat)
                break;

            var reduced = CashuMeltFeePolicy.ReducedForwardSat(spendableSat, quote.FeeReserve, forwardSat);
            if (attempt >= 3 || reduced is null)
                return; // reserve too large for the accumulated amount - try again once it grows
            forwardSat = reduced.Value;
            quote = null;
        }
        if (quote is null)
            return;

        BlindedMessage[]? blankOutputs = null;
        BlankOutputBlinding[]? blinding = null;
        if (quote.FeeReserve > 0)
            (blankOutputs, blinding) = CreateBlankOutputs(quote.FeeReserve, proofs);

        // Durable marker before the melt so a crash leaves rows in SWEEPING for recovery.
        foreach (var r in rows)
        {
            r.State = "SWEEPING";
            r.SweepReference = quote.Quote;
            r.SweepIntentJson = _assets.Protect(JsonSerializer.Serialize(new SweepIntent(quote.Quote, payoutInvoice!, blinding)));
        }
        await ctx.SaveChangesAsync(ct);

        MeltTokensResponse? resp;
        try
        {
            resp = await _mintClient.MeltTokensAsync(mintUrl, quote.Quote, proofs, blankOutputs, ct);
        }
        catch (CashuMeltMintProtocolException ex) when (ex.MintErrorCode == CashuMeltMintProtocolException.TokenAlreadySpent)
        {
            return; // Never infer payout; reconcile the exact stored quote on the next tick.
        }
        catch (Exception)
        {
            return; // rows stay SWEEPING; RecoverStuckSweepsAsync reconciles next tick
        }

        // Even a successful POST must be reconciled against its persisted quote. This also
        // recovers change-of-change after a crash without inventing another payout.
        try
        {
            var paidQuote = await _mintClient.GetMeltQuoteAsync(mintUrl, quote.Quote, ct);
            await CompleteSweepAsync(ctx, rows, new SweepIntent(quote.Quote, payoutInvoice!, blinding), paidQuote!, ct);
        }
        catch (Exception ex) { _logger.LogWarning("Change settlement confirmation deferred: {ErrorType}", ex.GetType().Name); }
    }

    /// <summary>
    /// NUT-02: highest input_fee_ppk among the keysets the proofs belong to.
    /// Returns 0 when the mint does not expose keyset metadata (pre-NUT-02 mints:
    /// 404/501/405 - no input fees exist), null on transient failures (retry later).
    /// </summary>
    private async Task<long?> TryGetMaxInputFeePpkAsync(
        string mintUrl, CashuMeltProof[] proofs, CancellationToken ct)
    {
        try
        {
            var keysetsInfo = await _mintClient.GetKeysetsInfoAsync(mintUrl, ct);
            var usedKeysetIds = proofs.Select(p => p.Id).Distinct().ToHashSet();
            return keysetsInfo?.Keysets?
                .Where(k => usedKeysetIds.Contains(k.Id))
                .Max(k => (long?)(k.InputFeePpk ?? 0)) ?? 0;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed)
        {
            return 0; // mint predates NUT-02 keyset metadata - it charges no input fees
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /v1/keysets unavailable at {MintUrl}", mintUrl);
            return null;
        }
    }

    /// <summary>
    /// NUT-07 aggregate proof state at the mint: "SPENT" (all spent), "PENDING" (any pending
    /// or partially spent), "UNSPENT" (all unspent), or null when it could not be determined.
    /// </summary>
    private async Task<string?> TryGetAggregateProofStateAsync(
        string mintUrl, CashuMeltProof[] proofs, CancellationToken ct)
    {
        try
        {
            var ys = proofs
                .Select(p => CashuMeltCrypto.ComputeYHex(System.Text.Encoding.UTF8.GetBytes(p.Secret)))
                .ToArray();
            var resp = await _mintClient.CheckProofStatesAsync(mintUrl, ys, ct);
            var states = resp?.States;
            // A partial response must not be aggregated - every requested Y needs a state,
            // otherwise "all spent"/"all unspent" conclusions would be drawn from a subset.
            if (states is null || states.Length != ys.Length
                || states.Select(s => s.Y).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ys.Length
                || !ys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(states.Select(s => s.Y))
                || states.Any(s => s.State is not ("UNSPENT" or "PENDING" or "SPENT")))
                return null;

            var spent = states.Count(s => string.Equals(s.State, "SPENT", StringComparison.OrdinalIgnoreCase));
            var pending = states.Count(s => string.Equals(s.State, "PENDING", StringComparison.OrdinalIgnoreCase));
            if (pending > 0)
                return "PENDING";
            if (spent == states.Length)
                return "SPENT";
            if (spent == 0)
                return "UNSPENT";
            return "PENDING"; // partially spent - in flight or anomalous; retry later
        }
        catch (Exception ex)
        {
            // NUT-07 is optional for callers - unknown state falls back to other signals.
            _logger.LogDebug(ex, "NUT-07 checkstate unavailable at {MintUrl}", mintUrl);
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the CashuMelt payment in BTCPay only after the mint has paid the merchant's invoice (melt OK).
    /// Returns false if the row could not be inserted and could not be found (retry later).
    /// </summary>
    private async Task<bool> TryRecordPaymentInBtcPayAsync(
        CashuMeltPaymentRequest req,
        InvoiceEntity invoice,
        CancellationToken ct)
    {
        if (!_handlers.TryGetValue(CashuMeltPlugin.CashuMeltPaymentMethodId, out var handler))
        {
            _logger.LogWarning(
                "CashuMelt payment handler missing; cannot record BTCPay payment for invoice {InvoiceId} quote {QuoteId}",
                req.InvoiceId, req.QuoteId);
            return false;
        }

        var paymentData = new CashuMeltPaymentData
        {
            QuoteId = req.QuoteId,
            AmountSats = req.AmountSats,
            Unit = req.Unit,
            Bolt11Invoice = req.Bolt11Invoice
        };

        var amountDecimal = req.Unit == "usd"
            ? (decimal)req.AmountSats / 100m
            : (decimal)req.AmountSats / 100_000_000m;

        var payment = new PaymentData
        {
            Id = req.QuoteId,
            InvoiceDataId = req.InvoiceId,
            Currency = req.Unit == "usd" ? "USD" : "BTC",
            Amount = amountDecimal,
            Status = PaymentStatus.Settled,
            Created = DateTimeOffset.UtcNow
        };
        payment.Set(invoice, handler, paymentData);

        PaymentEntity? paymentEntity;
        try
        {
            paymentEntity = await _paymentService.AddPayment(payment, [req.QuoteId]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AddPayment threw for invoice {InvoiceId} quote {QuoteId}",
                req.InvoiceId, req.QuoteId);
            paymentEntity = null;
        }

        if (paymentEntity is null)
        {
            var after = await _invoiceRepository.GetInvoice(req.InvoiceId);
            paymentEntity = after?.GetPayments(false)
                .FirstOrDefault(p =>
                    p.Id == req.QuoteId && p.PaymentMethodId == CashuMeltPlugin.CashuMeltPaymentMethodId);
            if (paymentEntity is null)
            {
                _logger.LogWarning(
                    "BTCPay payment not present after AddPayment for invoice {InvoiceId} quote {QuoteId}",
                    req.InvoiceId, req.QuoteId);
                return false;
            }
        }

        var updatedInvoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
        if (updatedInvoice is not null)
        {
            _eventAggregator.Publish(new InvoiceEvent(updatedInvoice, InvoiceEvent.ReceivedPayment) { Payment = paymentEntity });
        }

        _logger.LogInformation(
            "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} unit={Unit}",
            CashuMeltObservability.TagBtcpayRecorded,
            CashuMeltObservability.PhaseBtcpay,
            req.InvoiceId,
            req.QuoteId,
            req.AmountSats,
            req.Unit);

        return true;
    }

    private async Task<CashuMeltDbContext> CreateReadyContextAsync(CancellationToken ct)
    {
        var ctx = _dbContextFactory.CreateContext();
        try
        {
            _ = await ctx.CashuMeltStoreSettings.AnyAsync(ct);
            return ctx;
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx, ct);
            return ctx;
        }
    }

    private async Task WithSchemaRetryAsync(Func<CashuMeltDbContext, Task> action, CancellationToken ct)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        try
        {
            await action(ctx);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx, ct);
            await using var retry = _dbContextFactory.CreateContext();
            await action(retry);
        }
    }
}
