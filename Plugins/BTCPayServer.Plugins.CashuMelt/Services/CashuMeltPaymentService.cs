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
            string? knownProofState = null;
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

                // NUT-07 checkstate as the fallback source of truth for the proofs.
                knownProofState = await TryGetAggregateProofStateAsync(settings.MintUrl, proofs, ct);
                switch (knownProofState)
                {
                    case "PENDING":
                        return (false, null, true);
                    case "UNSPENT":
                        priorQuote = null; // proofs untouched - safe to melt fresh
                        break;
                    case "SPENT":
                        priorQuote = null; // completed below via the FreshMelt checkstate branch
                        break;
                    default:
                        // Row stays PENDING (no MarkFailed), but do not schedule another
                        // checkout poll for a state check the mint keeps refusing -
                        // background reconciliation picks the row up later.
                        return (false, null, false);
                }
            }

            switch (ClassifyPriorMeltQuote(priorQuote))
            {
                case PriorMeltQuoteDecision.CompleteSettlement:
                    _logger.LogInformation(
                        "Prior melt quote {MeltQuoteId} already PAID for invoice {InvoiceId} - completing settlement without a second melt",
                        req.MeltQuoteId, req.InvoiceId);
                    await TryStoreChangeProofsAsync(settings, req, priorQuote!.Change, ctx, ct);
                    return await CompleteMeltAsync(req, ctx, priorQuote.PaymentPreimage, priorQuote.Amount, ct);
                case PriorMeltQuoteDecision.WaitPending:
                    return (false, null, true);
            }

            // FreshMelt: the prior quote is gone / UNPAID / expired. The proofs may still have
            // been spent by an even earlier attempt whose quote id was lost - NUT-07 checkstate
            // is the authoritative signal before melting again.
            var proofState = knownProofState ?? await TryGetAggregateProofStateAsync(settings.MintUrl, proofs, ct);
            if (proofState == "SPENT")
            {
                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} msg=proofs_spent_checkstate_reconciled",
                    CashuMeltObservability.TagForwardOk,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    totalMintedSat);
                req.BlankOutputsJson = null;
                return await CompleteMeltAsync(req, ctx, preimage: null, forwardSat: null, ct);
            }
            if (proofState == "PENDING")
                return (false, null, true);
            // UNSPENT / unknown: proceed with a fresh melt.
        }

        // NUT-02: keyset input fee for spending the minted proofs - without it a
        // fee-charging mint rejects the melt for insufficient inputs.
        long keysetInputFeeSat;
        try
        {
            var keysetsInfo = await _mintClient.GetKeysetsInfoAsync(settings.MintUrl, ct);
            var usedKeysetIds = proofs.Select(p => p.Id).Distinct().ToHashSet();
            var maxPpk = keysetsInfo?.Keysets?
                .Where(k => usedKeysetIds.Contains(k.Id))
                .Max(k => (long?)(k.InputFeePpk ?? 0)) ?? 0;
            keysetInputFeeSat = CashuMeltFeePolicy.KeysetInputFeeSat(proofs.Length, maxPpk);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} msg=keysets_info_unavailable",
                CashuMeltObservability.TagMeltRetry,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId);
            return (false, null, true);
        }

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
                req.BlankOutputsJson = JsonSerializer.Serialize(blinding, ProofJsonOptions);
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
        await ctx.SaveChangesAsync(ct);

        MeltTokensResponse? meltResp;
        try
        {
            meltResp = await _mintClient.MeltTokensAsync(settings.MintUrl, meltQuote.Quote, proofs, blankOutputs, ct);
        }
        catch (CashuMeltMintProtocolException ex) when (ex.MintErrorCode == CashuMeltMintProtocolException.TokenAlreadySpent)
        {
            // The mint redeems proofs exactly once and these proofs never left this plugin,
            // so "already spent" means an earlier melt attempt succeeded and the merchant was
            // paid - only the confirmation was lost (crash or misread response). NUT-07
            // checkstate double-checks that conclusion before finalizing. The preimage is
            // unrecoverable because the paid melt quote id was since overwritten, and the
            // settled amount belongs to that prior melt - this attempt's forwardSat is only
            // logged as the attempted amount, not as the confirmed one.
            var alreadySpentState = await TryGetAggregateProofStateAsync(settings.MintUrl, proofs, ct);
            if (alreadySpentState is "PENDING" or "UNSPENT")
            {
                _logger.LogWarning(
                    "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} proofState={ProofState} msg=already_spent_checkstate_conflict",
                    CashuMeltObservability.TagMeltRetry,
                    CashuMeltObservability.PhaseForward,
                    req.InvoiceId,
                    req.QuoteId,
                    alreadySpentState);
                return (false, null, true);
            }

            _logger.LogWarning(
                "{Event} phase={Phase} invoice={InvoiceId} quote={QuoteId} amountSat={AmountSat} attemptedForwardSat={AttemptedForwardSat} msg=proofs_already_spent_reconciled",
                CashuMeltObservability.TagForwardOk,
                CashuMeltObservability.PhaseForward,
                req.InvoiceId,
                req.QuoteId,
                totalMintedSat,
                forwardSat);
            // This attempt's blank outputs were never used (the melt was rejected).
            req.BlankOutputsJson = null;
            return await CompleteMeltAsync(req, ctx, preimage: null, forwardSat: null, ct);
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

        if (!MeltStatePaid(meltResp.State) && !meltResp.Paid)
        {
            const string err = "Mint did not confirm Lightning payment";
            await MarkFailedAsync(ctx, req, CashuMeltObservability.PhaseForward, err, ct,
                CashuMeltFailureReasons.MeltFailed);
            return (false, err, false);
        }

        await TryStoreChangeProofsAsync(settings, req, meltResp.Change, ctx, ct);
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
        req.SettlementReference = preimage;
        req.MintedProofsJson = null;
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
    public sealed record BlankOutputBlinding(string Secret, string R, string KeysetId);

    /// <summary>
    /// NUT-08: blank outputs (amount 1, ignored by the mint) with fresh random secrets,
    /// using the keyset the proofs were minted with. Blinding data is returned for
    /// persistence so change signatures can be unblinded later.
    /// </summary>
    private static (BlindedMessage[] Outputs, BlankOutputBlinding[] Blinding) CreateBlankOutputs(
        long feeReserve, CashuMeltProof[] proofs)
    {
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
            blinding[i] = new BlankOutputBlinding(secretHex, Convert.ToHexString(r).ToLowerInvariant(), keysetId);
        }
        return (outputs, blinding);
    }

    /// <summary>
    /// Unblinds NUT-08 change signatures using the persisted blank-output blinding data and
    /// stores them as change proofs for the sweep. Best-effort: a failure is logged and the
    /// blinding data retained for manual recovery - it never blocks the settlement.
    /// </summary>
    private async Task TryStoreChangeProofsAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        BlindSignature[]? change,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.BlankOutputsJson))
            return;

        if (change is null || change.Length == 0)
        {
            req.BlankOutputsJson = null; // mint kept the whole reserve - nothing to recover
            return;                      // caller's save persists the clear
        }

        try
        {
            var blinding = JsonSerializer.Deserialize<BlankOutputBlinding[]>(req.BlankOutputsJson, ProofJsonOptions);
            if (blinding is null || blinding.Length == 0)
            {
                req.BlankOutputsJson = null;
                return;
            }

            var keysResp = await _mintClient.GetKeysAsync(settings.MintUrl, ct);
            var rows = UnblindChangeToRows(
                req.StoreId, CashuMeltMintPolicy.NormalizeMintUrl(settings.MintUrl), req.Unit,
                req.QuoteId, change, blinding, keysResp);
            ctx.CashuMeltChangeProofs.AddRange(rows);

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
            // Detach any staged change rows so a failed insert (e.g. duplicate secret)
            // cannot poison the settlement save that follows.
            foreach (var entry in ctx.ChangeTracker.Entries<CashuMeltChangeProof>().ToList())
                entry.State = EntityState.Detached;
        }
    }

    /// <summary>Unblinds NUT-08 change signatures into change-proof rows (invalid outputs skipped).</summary>
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
        for (var i = 0; i < change.Length && i < blinding.Length; i++)
        {
            var sig = change[i];
            if (sig.Amount <= 0)
                continue;

            var keyset = keysResp?.Keysets?.FirstOrDefault(k => k.Id == sig.Id);
            if (keyset is null || !keyset.Keys.TryGetValue(sig.Amount.ToString(), out var mintKeyHex))
            {
                _logger.LogWarning(
                    "No mint key for change denomination {Amount} in keyset {KeysetId}; skipping change output",
                    sig.Amount, sig.Id);
                continue;
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

    /// <summary>SWEEPING rows at tick start are always stale (sweeps are sequential) - reconcile via NUT-07.</summary>
    private async Task RecoverStuckSweepsAsync(CashuMeltDbContext ctx, CancellationToken ct)
    {
        var stuck = await ctx.CashuMeltChangeProofs
            .Where(p => p.State == "SWEEPING")
            .OrderBy(p => p.Id)
            .Take(500)
            .ToListAsync(ct);
        if (stuck.Count == 0)
            return;

        foreach (var grp in stuck.GroupBy(p => new { p.StoreId, p.MintUrl }))
        {
            var proofs = grp.Select(r => new CashuMeltProof(r.Amount, r.KeysetId, r.Secret, r.C)).ToArray();
            var state = await TryGetAggregateProofStateAsync(grp.Key.MintUrl, proofs, ct);
            foreach (var r in grp)
            {
                if (state == "SPENT")
                {
                    r.State = "SWEPT";
                    r.SweptAt = DateTimeOffset.UtcNow;
                }
                else if (state == "UNSPENT")
                {
                    r.State = "AVAILABLE";
                    r.SweepReference = null;
                }
                // PENDING / unknown: leave for the next tick.
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    private async Task SweepStoreChangeAsync(
        string storeId, string mintUrl, CashuMeltDbContext ctx, CancellationToken ct)
    {
        var settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.LightningAddress))
            return;
        if (CashuMeltMintPolicy.NormalizeMintUrl(settings.MintUrl) != mintUrl)
            return; // store switched mints - keep the proofs until reconfigured back

        var rows = await ctx.CashuMeltChangeProofs
            .Where(p => p.StoreId == storeId && p.MintUrl == mintUrl && p.State == "AVAILABLE" && p.Unit == "sat")
            .OrderBy(p => p.Id)
            .Take(200)
            .ToListAsync(ct);
        var totalSat = rows.Sum(r => r.Amount);
        if (totalSat < MinChangeSweepSat)
            return;

        var proofs = rows.Select(r => new CashuMeltProof(r.Amount, r.KeysetId, r.Secret, r.C)).ToArray();

        var keysetsInfo = await _mintClient.GetKeysetsInfoAsync(mintUrl, ct);
        var usedKeysetIds = proofs.Select(p => p.Id).Distinct().ToHashSet();
        var maxPpk = keysetsInfo?.Keysets?
            .Where(k => usedKeysetIds.Contains(k.Id))
            .Max(k => (long?)(k.InputFeePpk ?? 0)) ?? 0;
        var spendableSat = totalSat - CashuMeltFeePolicy.KeysetInputFeeSat(proofs.Length, maxPpk);

        var forwardSat = spendableSat - CashuMeltFeePolicy.EstimateFeeBufferSat(spendableSat);
        if (forwardSat <= 0)
            return;

        var lnResolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
        MeltQuoteResponse? quote = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (bolt11, effectiveSat) = await lnResolver.ResolveInvoiceAsync(
                settings.LightningAddress!.Trim(), forwardSat, spendableSat, ct);
            forwardSat = effectiveSat;

            quote = await _mintClient.RequestMeltQuoteAsync(mintUrl, bolt11, "sat", ct);
            if (quote is null)
                return;
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
        }
        await ctx.SaveChangesAsync(ct);

        MeltTokensResponse? resp;
        try
        {
            resp = await _mintClient.MeltTokensAsync(mintUrl, quote.Quote, proofs, blankOutputs, ct);
        }
        catch (CashuMeltMintProtocolException ex) when (ex.MintErrorCode == CashuMeltMintProtocolException.TokenAlreadySpent)
        {
            foreach (var r in rows)
            {
                r.State = "SWEPT";
                r.SweptAt = DateTimeOffset.UtcNow;
            }
            await ctx.SaveChangesAsync(ct);
            return;
        }
        catch (Exception)
        {
            return; // rows stay SWEEPING; RecoverStuckSweepsAsync reconciles next tick
        }

        if (resp is not null && MeltStatePending(resp.State))
            return; // leave SWEEPING; recovery resolves once the payment lands

        var paid = resp is not null && (MeltStatePaid(resp.State) || resp.Paid);
        if (!paid)
        {
            foreach (var r in rows)
            {
                r.State = "AVAILABLE";
                r.SweepReference = null;
            }
            await ctx.SaveChangesAsync(ct);
            return;
        }

        var preimage = resp!.PaymentPreimage ?? resp.Proof;
        foreach (var r in rows)
        {
            r.State = "SWEPT";
            r.SweptAt = DateTimeOffset.UtcNow;
            r.SweepReference = preimage ?? quote.Quote;
        }

        if (resp.Change is { Length: > 0 } && blinding is not null)
        {
            var keysResp = await _mintClient.GetKeysAsync(mintUrl, ct);
            ctx.CashuMeltChangeProofs.AddRange(
                UnblindChangeToRows(storeId, mintUrl, "sat", null, resp.Change, blinding, keysResp));
        }
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Event} store={StoreId} sweptSat={SweptSat} forwardSat={ForwardSat} proofCount={ProofCount} preimage={HasPreimage}",
            CashuMeltObservability.TagChangeSwept,
            storeId,
            totalSat,
            forwardSat,
            rows.Count,
            !string.IsNullOrEmpty(preimage));
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
            if (states is null || states.Length == 0)
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
