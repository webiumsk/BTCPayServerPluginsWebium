using System;
using System.Collections.Concurrent;
using System.Linq;
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
///   5. Payment is recorded in BTCPay.
///
/// Proofs are persisted to DB after step 3 and cleared after step 4,
/// enabling crash-safe retry of the melt without re-minting.
/// </summary>
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

        _logger.LogInformation("Quote {QuoteId} created for invoice {InvoiceId} ({Amount} {Unit})",
            quote.Quote, invoiceId, amountSats, unit);

        return (quote.Quote, quote.Request, null);
    }

    // ──────────────────────────────────────────────────────────────
    // Payment detection → mint → melt (called from poll endpoint)
    // ──────────────────────────────────────────────────────────────

    public async Task<(bool Paid, string? Error)> CheckAndRecordPaymentAsync(
        string quoteId, CancellationToken ct = default)
    {
        // Serialize concurrent poll requests for the same quote to prevent duplicate minting.
        var sem = _quoteProcessingLocks.GetOrAdd(quoteId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
            return (false, null); // Another request is already processing this quote

        try
        {

        await using var ctx = await CreateReadyContextAsync(ct);

        var req = await ctx.CashuMeltPaymentRequests.FirstOrDefaultAsync(r => r.QuoteId == quoteId, ct);
        if (req is null) return (false, "Quote not found");

        // Already finished
        if (req.SettlementState == "SETTLED") return (true, null);
        if (req.SettlementState == "FAILED")  return (false, req.SettlementError);

        var settings = await _configService.GetEnabledSettingsAsync(req.StoreId, ct);
        if (settings is null) return (false, "Store CashuMelt settings not found");

        if (string.IsNullOrWhiteSpace(settings.LightningAddress))
            return (false, "No Lightning address configured for merchant payout");

        // 1. Poll mint for quote state
        var mintQuote = await _mintClient.GetMintQuoteAsync(settings.MintUrl, quoteId, ct);
        if (mintQuote?.State is not ("PAID" or "ISSUED"))
            return (false, null); // not paid yet

        var invoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
        if (invoice is null) return (false, "BTCPay invoice not found");

        // Invoice already finalised by another payment method.
        // Note: InvoiceStatus.Expired is intentionally NOT listed here – we still need
        // to melt proofs to the merchant even if the BTCPay invoice expired.
        if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Invalid)
        {
            req.State = mintQuote.State;
            req.PaidAt ??= DateTimeOffset.UtcNow;
            req.SettlementState = "SETTLED";
            await ctx.SaveChangesAsync(ct);
            return (true, null);
        }

        // Record payment in BTCPay IMMEDIATELY – before the mint/melt HTTP calls (5–15 s).
        // This prevents the invoice from transitioning to "Expired (paid late)" if the HTTP
        // calls take longer than the remaining invoice lifetime.
        await RecordPaymentInBtcPayAsync(req, invoice, ct);
        req.State  = mintQuote.State;
        req.PaidAt ??= DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        // 2. Determine which proofs to use:
        //    a) Crash recovery: proofs were already minted but melt failed last time.
        //    b) Normal path: mint proofs from scratch.
        CashuMeltProof[] proofs;

        if (!string.IsNullOrEmpty(req.MintedProofsJson))
        {
            _logger.LogInformation("Retrying melt for quote {QuoteId} (proofs already in DB)", quoteId);
            proofs = JsonSerializer.Deserialize<CashuMeltProof[]>(req.MintedProofsJson, ProofJsonOptions)
                     ?? throw new InvalidOperationException("Stored proofs JSON is corrupt");
        }
        else
        {
            var (mintedProofs, mintError) = await MintProofsAsync(settings, req, mintQuote.State, ctx, ct);
            if (mintError is not null) return (false, mintError);
            proofs = mintedProofs!;
        }

        // 3. Melt proofs to merchant's Lightning address
        var (settled, meltError) = await MeltToMerchantAsync(settings, req, invoice, proofs, ctx, ct);
        return settled ? (true, null) : (false, meltError);

        } // end try
        finally
        {
            sem.Release();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Step 2: Mint proofs (NUT-05)
    // ──────────────────────────────────────────────────────────────

    private async Task<(CashuMeltProof[]? Proofs, string? Error)> MintProofsAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        string mintQuoteState,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        // Fetch active keyset for the requested unit
        var keysResp = await _mintClient.GetKeysAsync(settings.MintUrl, ct);
        var keyset = keysResp?.Keysets?.FirstOrDefault(k =>
            string.Equals(k.Unit, req.Unit, StringComparison.OrdinalIgnoreCase));

        if (keyset is null)
            return (null, $"No keyset for unit '{req.Unit}' found on mint {settings.MintUrl}");

        // Build blinded outputs (one per power-of-2 denomination)
        var denominations = CashuMeltCrypto.DecomposeAmount(req.AmountSats);
        if (denominations.Length == 0)
            return (null, $"Cannot decompose amount {req.AmountSats}");

        // blindingData: (secretHex string, blinding scalar r bytes)
        var blindingData = new (string secretHex, byte[] r)[denominations.Length];
        var outputs = new BlindedMessage[denominations.Length];

        for (int i = 0; i < denominations.Length; i++)
        {
            var denom = denominations[i];
            var denomKey = denominations[i].ToString();

            if (!keyset.Keys.TryGetValue(denomKey, out var mintPubKeyHex))
                return (null, $"Mint has no key for denomination {denom} in keyset {keyset.Id}");

            // CashuMelt NUT-00: secret is a hex string; hash_to_curve receives its UTF-8 bytes.
            var secretHex = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLower();
            var secretUtf8 = System.Text.Encoding.UTF8.GetBytes(secretHex);
            var (B_Hex, r) = CashuMeltCrypto.CreateBlindedMessage(secretUtf8);

            blindingData[i] = (secretHex, r);
            outputs[i] = new BlindedMessage(denom, keyset.Id, B_Hex);
        }

        // Send blinded messages to mint; receive blind signatures C_
        MintTokensResponse? mintResp;
        try
        {
            mintResp = await _mintClient.MintTokensAsync(settings.MintUrl, req.QuoteId, outputs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MintTokens call failed for quote {QuoteId}", req.QuoteId);
            return (null, $"Mint refused to issue tokens: {ex.Message}");
        }

        if (mintResp?.Signatures is null || mintResp.Signatures.Length != denominations.Length)
            return (null, "Mint returned unexpected number of signatures");

        // Unblind signatures: C = C_ - r·K
        var proofs = new CashuMeltProof[denominations.Length];
        for (int i = 0; i < denominations.Length; i++)
        {
            var sig    = mintResp.Signatures[i];
            var denom  = denominations[i].ToString();

            if (!keyset.Keys.TryGetValue(denom, out var mintPubKeyHex))
                return (null, $"Keyset missing key for denomination {denominations[i]}");

            var (secretHex, r) = blindingData[i];
            var C_hex          = sig.C_;
            var CHex           = CashuMeltCrypto.UnblindSignature(C_hex, mintPubKeyHex, r);

            proofs[i] = new CashuMeltProof(sig.Amount, sig.Id, secretHex, CHex);
        }

        // Persist proofs BEFORE attempting melt (crash safety)
        req.MintedProofsJson = JsonSerializer.Serialize(proofs, ProofJsonOptions);
        req.State  = mintQuoteState;
        req.PaidAt ??= DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation("Minted {Count} proofs ({TotalSat} sat) for quote {QuoteId}",
            proofs.Length, proofs.Sum(p => p.Amount), req.QuoteId);

        return (proofs, null);
    }

    // ──────────────────────────────────────────────────────────────
    // Step 3: Melt proofs to merchant Lightning address (NUT-14)
    // ──────────────────────────────────────────────────────────────

    private async Task<(bool Settled, string? Error)> MeltToMerchantAsync(
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest req,
        InvoiceEntity invoice,
        CashuMeltProof[] proofs,
        CashuMeltDbContext ctx,
        CancellationToken ct)
    {
        var totalMintedSat = proofs.Sum(p => p.Amount);

        // Reserve a fee buffer so we have enough proofs to cover LN routing fees.
        // The buffer is deducted from the forwarded amount; any leftover from the
        // mint's feeReserve comes back as change proofs (currently discarded – see TODO).
        long feeBuffer = FeeBuffer(totalMintedSat);
        long forwardSat = totalMintedSat - feeBuffer;

        if (forwardSat <= 0)
        {
            req.SettlementState = "FAILED";
            req.SettlementError = $"Amount ({totalMintedSat} sat) too small to cover routing fee buffer ({feeBuffer} sat)";
            await ctx.SaveChangesAsync(ct);
            return (false, req.SettlementError);
        }

        // Resolve merchant's Lightning address → BOLT11.
        // Pass totalMintedSat as the upper bound so the resolver can clamp the amount
        // up to the LNURL minimum when forwardSat is below it.
        string bolt11;
        try
        {
            var lnResolver = new LightningAddressResolver(_httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
            var (resolvedBolt11, effectiveSat) = await lnResolver.ResolveInvoiceAsync(
                settings.LightningAddress!.Trim(), forwardSat, totalMintedSat, ct);
            bolt11 = resolvedBolt11;

            if (effectiveSat != forwardSat)
                _logger.LogInformation(
                    "Forward amount adjusted from {Desired} to {Effective} sat to meet LNURL limits for quote {QuoteId}",
                    forwardSat, effectiveSat, req.QuoteId);

            forwardSat = effectiveSat;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LNURL resolution failed for address {Addr}", settings.LightningAddress);
            req.SettlementState = "FAILED";
            req.SettlementError = $"Could not resolve Lightning address: {ex.Message}";
            await ctx.SaveChangesAsync(ct);
            return (false, req.SettlementError);
        }

        // Persist the BOLT11 so a retry can reuse it
        if (req.ForwardBolt11 != bolt11)
        {
            req.ForwardBolt11 = bolt11;
            await ctx.SaveChangesAsync(ct);
        }

        // Request melt quote from mint
        MeltQuoteResponse? meltQuote;
        try
        {
            meltQuote = await _mintClient.RequestMeltQuoteAsync(settings.MintUrl, bolt11, req.Unit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestMeltQuote failed for quote {QuoteId}", req.QuoteId);
            return (false, $"Melt quote request failed: {ex.Message}");
        }

        if (meltQuote is null)
            return (false, "Mint returned no melt quote");

        long totalNeeded = meltQuote.Amount + meltQuote.FeeReserve;
        if (totalNeeded > totalMintedSat)
        {
            // Fee reserve exceeds our buffer – reduce forward amount and retry once
            _logger.LogWarning("FeeReserve {Fee} > buffer {Buf} for quote {Q}; reducing forward amount",
                meltQuote.FeeReserve, feeBuffer, req.QuoteId);

            // Fallback: just let the melt proceed with all available proofs.
            // The merchant gets forwardSat - (actualFee - feeBuffer) sat.
            // This path is rare; a proper implementation would re-request the BOLT11
            // for the reduced amount, which requires another LNURL call.
            // TODO: implement iterative amount adjustment.
            return (false,
                $"LN routing fee ({meltQuote.FeeReserve} sat) exceeds buffer ({feeBuffer} sat). " +
                "Increase the fee buffer or retry.");
        }

        req.MeltQuoteId = meltQuote.Quote;
        await ctx.SaveChangesAsync(ct);

        // Execute melt – mint pays the Lightning invoice using our proofs
        MeltTokensResponse? meltResp;
        try
        {
            meltResp = await _mintClient.MeltTokensAsync(settings.MintUrl, meltQuote.Quote, proofs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MeltTokens call failed for quote {QuoteId}", req.QuoteId);
            // Proofs are still in DB; melt will be retried on next poll
            return (false, $"Melt execution failed: {ex.Message}");
        }

        if (meltResp is null || !meltResp.Paid)
        {
            var err = "Mint did not confirm payment";
            req.SettlementState = "FAILED";
            req.SettlementError = err;
            await ctx.SaveChangesAsync(ct);
            return (false, err);
        }

        // ── Success ───────────────────────────────────────────────────────────
        // (AddPayment to BTCPay was already called in CheckAndRecordPaymentAsync,
        //  immediately after detecting the PAID mint quote state.)

        // Mark settled; clear stored proofs (they are now spent)
        req.SettlementState     = "SETTLED";
        req.SettlementReference = meltResp.Proof; // payment preimage
        req.SettledAt           = DateTimeOffset.UtcNow;
        req.MintedProofsJson    = null;  // proofs spent, no longer needed
        await ctx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CashuMelt payment settled for invoice {InvoiceId}: {Amount} sat → {Addr}. Preimage: {Pre}",
            req.InvoiceId, forwardSat, settings.LightningAddress, meltResp.Proof);

        return (true, null);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the CashuMelt payment in BTCPay Server immediately upon detecting a PAID mint
    /// quote, before the mint/melt HTTP calls. This ensures the BTCPay invoice is marked
    /// as paid before it can expire, preventing "Expired (paid late)" invoice states.
    /// </summary>
    private async Task RecordPaymentInBtcPayAsync(
        CashuMeltPaymentRequest req,
        InvoiceEntity invoice,
        CancellationToken ct)
    {
        if (!_handlers.TryGetValue(CashuMeltPlugin.CashuMeltPaymentMethodId, out var handler))
        {
            _logger.LogWarning(
                "CashuMelt payment handler not found; cannot record payment in BTCPay for quote {QuoteId}",
                req.QuoteId);
            return;
        }

        var paymentData = new CashuMeltPaymentData
        {
            QuoteId       = req.QuoteId,
            AmountSats    = req.AmountSats,
            Unit          = req.Unit,
            Bolt11Invoice = req.Bolt11Invoice
        };

        var amountDecimal = req.Unit == "usd"
            ? (decimal)req.AmountSats / 100m           // cents → USD
            : (decimal)req.AmountSats / 100_000_000m;  // sat  → BTC

        var payment = new PaymentData
        {
            Id            = req.QuoteId,
            InvoiceDataId = req.InvoiceId,
            Currency      = req.Unit == "usd" ? "USD" : "BTC",
            Amount        = amountDecimal,
            Status        = PaymentStatus.Settled,
            Created       = DateTimeOffset.UtcNow
        };
        payment.Set(invoice, handler, paymentData);
        var paymentEntity = await _paymentService.AddPayment(payment, [req.QuoteId]);

        // Publish ReceivedPayment so InvoiceWatcher.Watch() is called and the invoice
        // state machine transitions New → Processing → Settled.
        // AddPayment only publishes PaymentSettled which InvoiceWatcher does NOT subscribe
        // to for state transitions — so we must publish this event ourselves.
        if (paymentEntity is not null)
        {
            // Re-fetch the invoice so it includes the new payment
            var updatedInvoice = await _invoiceRepository.GetInvoice(req.InvoiceId);
            if (updatedInvoice is not null)
                _eventAggregator.Publish(new InvoiceEvent(updatedInvoice, InvoiceEvent.ReceivedPayment) { Payment = paymentEntity });
        }

        _logger.LogInformation(
            "Recorded CashuMelt payment in BTCPay for invoice {InvoiceId} quote {QuoteId} ({Amount} {Unit})",
            req.InvoiceId, req.QuoteId, req.AmountSats, req.Unit);
    }

    /// <summary>
    /// Routing fee buffer: 1% of amount, minimum 2 sat, maximum 100 sat.
    /// The merchant receives (amount - feeBuffer) sat; any unused part of the
    /// mint's fee reserve comes back as change (currently discarded).
    /// </summary>
    private static long FeeBuffer(long amountSat)
        => Math.Min(100, Math.Max(2, (long)Math.Ceiling(amountSat * 0.01)));

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
