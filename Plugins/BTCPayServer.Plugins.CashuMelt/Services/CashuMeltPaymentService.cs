using System;
using System.Collections.Concurrent;
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
        ILogger<CashuMeltPaymentService> logger)
    {
        _mintClient = mintClient;
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

        var quote = await _mintClient.CreateMintQuoteAsync(settings.MintUrl, amountSats, unit, ct);
        if (quote is null || string.IsNullOrEmpty(quote.Quote))
            return (null, null, "Mint did not return a valid quote");

        var req = new CashuMeltPaymentRequest
        {
            QuoteId        = quote.Quote,
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
            quote.Quote, invoiceId, amountSats, unit);

        return (quote.Quote, quote.Request, null);
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

            var settings = await _configService.GetEnabledSettingsAsync(req.StoreId, ct);
            if (settings is null)
                return (false, "Store CashuMelt settings not found", null);

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

            var pollResult = await _mintClient.GetMintQuoteForPollAsync(settings.MintUrl, quoteId, ct);
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
            if (mintQuote.State is not ("PAID" or "ISSUED"))
                return (false, null, null);

            var invoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
            if (invoice is null)
                return (false, "BTCPay invoice not found", null);

            if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Invalid)
            {
                req.State = mintQuote.State;
                req.PaidAt ??= DateTimeOffset.UtcNow;
                req.SettlementState = "SETTLED";
                await ctx.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} invoiceStatus={InvoiceStatus}",
                    CashuMeltObservability.TagSkippedOtherPayment,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    quoteId,
                    req.AmountSats,
                    invoice.Status);
                return (true, null, null);
            }

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
                proofs = JsonSerializer.Deserialize<CashuMeltProof[]>(req.MintedProofsJson, ProofJsonOptions)
                         ?? throw new InvalidOperationException("Stored proofs JSON is corrupt");
            }
            else
            {
                var (mintedProofs, mintError, mintReasonCode) = await MintProofsAsync(settings, req, mintQuote.State, ctx, ct);
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
        await using var ctx = _dbContextFactory.CreateContext();
        var r = await ctx.CashuMeltPaymentRequests
            .FirstOrDefaultAsync(x => x.QuoteId == quoteId && x.StoreId == storeId, ct);

        if (r is null)
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.NotFound);

        if (r.SettlementState == "SETTLED")
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.AlreadySettled, Settled: true);

        if (r.SettlementState == "MELT_COMPLETE")
        {
            var (paidMc, errMc, raMc) = await CheckAndRecordPaymentAsync(quoteId, ct);
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.Completed, paidMc, errMc, raMc);
        }

        if (string.IsNullOrEmpty(r.MintedProofsJson) && r.SettlementState == "FAILED")
            return new CashuMeltRetryOutcome(CashuMeltRetryKind.CannotRetryMissingProofs);

        // Manual retry resets escalation state so background reconciliation can resume.
        r.SettlementState = "PENDING";
        r.SettlementError = null;
        r.NeedsManualReview = false;
        r.RetryCount = 0;
        r.FailureReasonCode = null;
        await ctx.SaveChangesAsync(ct);

        var (paid, error, retryAfter) = await CheckAndRecordPaymentAsync(quoteId, ct);
        return new CashuMeltRetryOutcome(CashuMeltRetryKind.Completed, paid, error, retryAfter);
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
        var keysResp = await _mintClient.GetKeysAsync(settings.MintUrl, ct);
        var keyset = keysResp?.Keysets?.FirstOrDefault(k =>
            string.Equals(k.Unit, req.Unit, StringComparison.OrdinalIgnoreCase));

        if (keyset is null)
            return (null, $"No keyset for unit '{req.Unit}' found on mint {settings.MintUrl}", CashuMeltFailureReasons.MintProofFailed);

        var denominations = CashuMeltCrypto.DecomposeAmount(req.AmountSats);
        if (denominations.Length == 0)
            return (null, $"Cannot decompose amount {req.AmountSats}", CashuMeltFailureReasons.MintProofFailed);

        var blindingData = new (string secretHex, byte[] r)[denominations.Length];
        var outputs = new BlindedMessage[denominations.Length];

        for (int i = 0; i < denominations.Length; i++)
        {
            var denom = denominations[i];
            var denomKey = denominations[i].ToString();

            if (!keyset.Keys.TryGetValue(denomKey, out _))
                return (null, $"Mint has no key for denomination {denom} in keyset {keyset.Id}", CashuMeltFailureReasons.MintProofFailed);

            var secretHex = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var secretUtf8 = System.Text.Encoding.UTF8.GetBytes(secretHex);
            var (B_Hex, r) = CashuMeltCrypto.CreateBlindedMessage(secretUtf8);

            blindingData[i] = (secretHex, r);
            outputs[i] = new BlindedMessage(denom, keyset.Id, B_Hex);
        }

        MintTokensResponse? mintResp;
        try
        {
            mintResp = await _mintClient.MintTokensAsync(settings.MintUrl, req.QuoteId, outputs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MintTokens call failed for invoice {InvoiceId} quote {QuoteId}", req.InvoiceId, req.QuoteId);
            return (null, $"Mint refused to issue tokens: {ex.Message}", CashuMeltFailureReasons.MintProofFailed);
        }

        if (mintResp?.Signatures is null || mintResp.Signatures.Length != denominations.Length)
            return (null, "Mint returned unexpected number of signatures", CashuMeltFailureReasons.MintProofFailed);

        // Verify all returned signatures belong to the expected keyset (keyset conflict detection).
        for (int i = 0; i < mintResp.Signatures.Length; i++)
        {
            if (mintResp.Signatures[i].Id != keyset.Id)
            {
                var conflict = $"Mint returned signature with unexpected keyset ID '{mintResp.Signatures[i].Id}' (expected '{keyset.Id}'). Refusing mint to prevent keyset collision.";
                _logger.LogWarning(
                    "cashumelt_keyset_conflict invoice={InvoiceId} quote={QuoteId} expectedKeysetId={Expected} actualKeysetId={Actual}",
                    req.InvoiceId, req.QuoteId, keyset.Id, mintResp.Signatures[i].Id);
                return (null, conflict, CashuMeltFailureReasons.KeysetConflict);
            }
        }

        var proofs = new CashuMeltProof[denominations.Length];
        for (int i = 0; i < denominations.Length; i++)
        {
            var sig = mintResp.Signatures[i];

            if (!keyset.Keys.TryGetValue(denominations[i].ToString(), out var mintPubKeyHex))
                return (null, $"Keyset missing key for denomination {denominations[i]}", CashuMeltFailureReasons.MintProofFailed);

            var (secretHex, r) = blindingData[i];
            var C_hex = sig.C_;
            var CHex = CashuMeltCrypto.UnblindSignature(C_hex, mintPubKeyHex, r);

            proofs[i] = new CashuMeltProof(sig.Amount, sig.Id, secretHex, CHex);
        }

        req.MintedProofsJson = JsonSerializer.Serialize(proofs, ProofJsonOptions);
        req.State = mintQuoteState;
        req.PaidAt ??= DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} proofCount={ProofCount}",
            CashuMeltObservability.TagMintProofOk,
            CashuMeltObservability.PhaseMintProof,
            req.InvoiceId,
            req.QuoteId,
            proofs.Sum(p => p.Amount),
            proofs.Length);

        return (proofs, null, null);
    }

    // ──────────────────────────────────────────────────────────────
    // Step 3: Melt proofs to merchant Lightning address (NUT-14)
    // ──────────────────────────────────────────────────────────────

    /// <summary>transientMelt: caller should poll again without surfacing a hard error to the payer.</summary>
    private async Task<(bool Ok, string? Error, bool TransientMelt)> MeltToMerchantAsync(
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
                priorQuote = null; // mint no longer knows the quote - safe to melt fresh
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

            if (priorQuote is not null && MeltStatePaid(priorQuote.State))
            {
                _logger.LogInformation(
                    "Prior melt quote {MeltQuoteId} already PAID for invoice {InvoiceId} - completing settlement without a second melt",
                    req.MeltQuoteId, req.InvoiceId);
                return await CompleteMeltAsync(req, ctx, priorQuote.PaymentPreimage, priorQuote.Amount, ct);
            }

            if (priorQuote is not null && MeltStatePending(priorQuote.State))
                return (false, null, true);
            // UNPAID / expired / unknown: proofs are unspent - proceed with a fresh melt.
        }

        long feeBuffer = CashuMeltFeePolicy.EstimateFeeBufferSat(totalMintedSat);
        long forwardSat = totalMintedSat - feeBuffer;

        if (forwardSat <= 0)
        {
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                $"Amount ({totalMintedSat} sat) too small to cover routing fee buffer ({feeBuffer} sat)",
                ct, CashuMeltFailureReasons.AmountTooSmall);
            return (false, req.SettlementError, false);
        }

        // The mint reveals its actual Lightning fee reserve only in the melt quote, so the
        // estimated buffer can undershoot. When it does, shrink the forwarded amount to
        // totalMinted - actual reserve, fetch a fresh invoice and re-quote.
        string bolt11;
        MeltQuoteResponse? meltQuote;
        const int maxFeeAdjustAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var lnResolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
                var (resolvedBolt11, effectiveSat) = await lnResolver.ResolveInvoiceAsync(
                    settings.LightningAddress!.Trim(), forwardSat, totalMintedSat, ct);
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
            if (totalNeeded <= totalMintedSat)
                break;

            var reducedForwardSat = CashuMeltFeePolicy.ReducedForwardSat(totalMintedSat, meltQuote.FeeReserve, forwardSat);
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

        req.MeltQuoteId = meltQuote.Quote;
        await ctx.SaveChangesAsync(ct);

        MeltTokensResponse? meltResp;
        try
        {
            meltResp = await _mintClient.MeltTokensAsync(settings.MintUrl, meltQuote.Quote, proofs, ct);
        }
        catch (CashuMeltMintProtocolException ex) when (ex.MintErrorCode == CashuMeltMintProtocolException.TokenAlreadySpent)
        {
            // The mint redeems proofs exactly once and these proofs never left this plugin,
            // so "already spent" means an earlier melt attempt succeeded and the merchant was
            // paid - only the confirmation was lost (crash or misread response). Complete the
            // settlement instead of retrying forever. The preimage is unrecoverable because
            // the paid melt quote id was since overwritten by newer attempts.
            _logger.LogWarning(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} msg=proofs_already_spent_reconciled",
                CashuMeltObservability.TagForwardOk,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                totalMintedSat);
            return await CompleteMeltAsync(req, ctx, preimage: null, forwardSat, ct);
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
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward,
                "Mint returned empty melt response", ct, CashuMeltFailureReasons.MeltFailed);
            return (false, "Mint returned empty melt response", false);
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

        if (!MeltStatePaid(meltResp.State) && !meltResp.Paid)
        {
            const string err = "Mint did not confirm Lightning payment";
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward, err, ct,
                CashuMeltFailureReasons.MeltFailed);
            return (false, err, false);
        }

        return await CompleteMeltAsync(req, ctx, meltResp.PaymentPreimage ?? meltResp.Proof, forwardSat, ct);
    }

    /// <summary>Marks the melt as done: stores the preimage, clears spent proofs.</summary>
    private async Task<(bool Ok, string? Error, bool TransientMelt)> CompleteMeltAsync(
        CashuMeltPaymentRequest req,
        CashuMeltDbContext ctx,
        string? preimage,
        long forwardSat,
        CancellationToken ct)
    {
        req.SettlementReference = preimage;
        req.MintedProofsJson = null;
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

    private static bool MeltStatePaid(string? state) =>
        string.Equals(state, "PAID", StringComparison.OrdinalIgnoreCase);

    private static bool MeltStatePending(string? state) =>
        string.Equals(state, "PENDING", StringComparison.OrdinalIgnoreCase);

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
