using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

public enum MatchOutcome
{
    /// <summary>No pending request carries this reference - ignored.</summary>
    UnknownReference,

    /// <summary>Already confirmed or duplicate delivery - dropped.</summary>
    Duplicate,

    /// <summary>Matched and settled in BTCPay.</summary>
    Settled,

    /// <summary>Reference matched but amount/currency did not - flagged for review, never auto-settled.</summary>
    ManualReview,
}

/// <summary>
/// The single matching rule for every confirmation backend: the reference
/// must match a pending request AND the credited amount must cover the due
/// amount (within the store's tolerance) in EUR. Anything else is flagged
/// as MANUAL_REVIEW - automated sources never settle a mismatched payment.
/// </summary>
public class SepaMatchingService
{
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaPaymentRecorder _paymentRecorder;
    private readonly ILogger<SepaMatchingService> _logger;

    public SepaMatchingService(
        SepaDbContextFactory dbContextFactory,
        SepaPaymentRecorder paymentRecorder,
        ILogger<SepaMatchingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _paymentRecorder = paymentRecorder;
        _logger = logger;
    }

    public async Task<MatchOutcome> ProcessAsync(
        string sourceId,
        ConfirmedPayment confirmation,
        decimal amountTolerance,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var request = await ctx.SepaPaymentRequests
            .FirstOrDefaultAsync(r => r.Reference == confirmation.Reference, cancellationToken);

        if (request is null)
        {
            _logger.LogInformation(
                "sepa_match_unknown source={Source} reference={Reference}",
                sourceId, confirmation.Reference);
            return MatchOutcome.UnknownReference;
        }

        if (request.State == SepaPaymentRequestState.Confirmed)
            return MatchOutcome.Duplicate;

        if (confirmation.DedupKey is not null)
        {
            if (request.DedupKey == confirmation.DedupKey)
                return MatchOutcome.Duplicate;
            request.DedupKey = confirmation.DedupKey;
        }

        var verdict = Evaluate(request.AmountDue, request.Currency, confirmation, amountTolerance);
        if (verdict is not null)
        {
            request.State = SepaPaymentRequestState.ManualReview;
            request.ReviewReason = verdict;
            request.RawConfirmationJson = confirmation.RawJson;
            await SaveDedupSafeAsync(ctx, cancellationToken);
            _logger.LogWarning(
                "sepa_match_review source={Source} reference={Reference} reason={Reason}",
                sourceId, confirmation.Reference, verdict);
            return MatchOutcome.ManualReview;
        }

        var recorded = await _paymentRecorder.RecordAsync(request, confirmation.Amount, cancellationToken);
        if (!recorded)
        {
            // Leave PENDING - the source may retry (poller) or the merchant
            // can settle manually; nothing is lost.
            await SaveDedupSafeAsync(ctx, cancellationToken);
            return MatchOutcome.UnknownReference;
        }

        request.State = SepaPaymentRequestState.Confirmed;
        request.ConfirmedAt = DateTimeOffset.UtcNow;
        request.ConfirmedBy = sourceId;
        request.RawConfirmationJson = confirmation.RawJson;
        await SaveDedupSafeAsync(ctx, cancellationToken);

        _logger.LogInformation(
            "sepa_match_settled source={Source} reference={Reference} invoice={InvoiceId}",
            sourceId, confirmation.Reference, request.InvoiceId);
        return MatchOutcome.Settled;
    }

    /// <summary>Null = match; otherwise the human-readable mismatch reason.</summary>
    internal static string? Evaluate(
        decimal amountDue,
        string requestCurrency,
        ConfirmedPayment confirmation,
        decimal amountTolerance)
    {
        if (!string.Equals(confirmation.Currency, requestCurrency, StringComparison.OrdinalIgnoreCase))
            return $"currency mismatch: expected {requestCurrency}, got {confirmation.Currency}";

        if (confirmation.Amount < amountDue - amountTolerance)
            return $"amount too low: due {amountDue:0.00}, received {confirmation.Amount:0.00}";

        return null;
    }

    private static async Task SaveDedupSafeAsync(SepaDbContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique DedupKey violation → a concurrent duplicate delivery
            // won the race; dropping this one is exactly the intent.
        }
    }
}
