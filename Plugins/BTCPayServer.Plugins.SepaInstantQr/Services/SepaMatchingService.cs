using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
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
/// Automatic confirmation binds the trusted source store and observed receiving
/// account to the immutable request before checking amount/currency or consuming
/// a bank event. Explicit merchant decisions use a separate manual entry point.
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

    public Task<MatchOutcome> ProcessAsync(
        string storeId,
        string sourceId,
        ConfirmedPayment confirmation,
        decimal amountTolerance,
        CancellationToken cancellationToken = default)
        => ProcessCoreAsync(storeId, sourceId, confirmation, amountTolerance, false, cancellationToken);

    /// <summary>Only the existing authorized manual controllers use this entry point.</summary>
    public async Task<MatchOutcome> ConfirmManuallyAsync(
        string storeId,
        string actorId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var request = await ctx.SepaPaymentRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Reference == reference && r.StoreId == storeId, cancellationToken);
        if (request is null)
            return MatchOutcome.UnknownReference;

        return await ProcessCoreAsync(storeId, $"manual:{actorId}",
            new ConfirmedPayment(reference, request.AmountDue, request.Currency, null, null),
            0m, true, cancellationToken);
    }

    private async Task<MatchOutcome> ProcessCoreAsync(
        string storeId,
        string sourceId,
        ConfirmedPayment confirmation,
        decimal amountTolerance,
        bool manual,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            return MatchOutcome.UnknownReference;

        await using var strategyContext = _dbContextFactory.CreateContext();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        // The host enables Npgsql connection retries. The entire user-managed
        // transaction must participate in that strategy, using fresh tracking
        // state on each retry. External BTCPay recording retains the same
        // reference-based identity if a plugin commit is retried afterward.
        return await strategy.ExecuteAsync(() => ProcessTransactionAsync(
            storeId, sourceId, confirmation, amountTolerance, manual, cancellationToken));
    }

    private async Task<MatchOutcome> ProcessTransactionAsync(
        string storeId,
        string sourceId,
        ConfirmedPayment confirmation,
        decimal amountTolerance,
        bool manual,
        CancellationToken cancellationToken)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
        // Serialize confirmations of this invoice. The existing unique dedup
        // index additionally serializes one bank event offered to two invoices.
        var request = await ctx.SepaPaymentRequests
            .FromSqlInterpolated($"SELECT * FROM \"BTCPayServer.Plugins.SepaInstantQr\".\"SepaPaymentRequests\" WHERE \"Reference\" = {confirmation.Reference} AND \"StoreId\" = {storeId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);

        if (request is null)
        {
            _logger.LogInformation(
                "sepa_match_unknown source={Source}",
                DiagnosticSource(sourceId, manual));
            return MatchOutcome.UnknownReference;
        }

        if (request.State == SepaPaymentRequestState.Confirmed)
            return MatchOutcome.Duplicate;

        var verdict = manual ? null : EvaluateBankEvidence(request, sourceId, confirmation);
        verdict ??= Evaluate(request.AmountDue, request.Currency, confirmation, amountTolerance);
        if (verdict is not null)
        {
            request.State = SepaPaymentRequestState.ManualReview;
            request.ReviewReason = verdict;
            request.RawConfirmationJson = confirmation.RawJson;
            // Invalid evidence never consumes a delivery id. A corrected
            // authenticated report may retry that same id after review.
            await ctx.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogWarning(
                "sepa_match_review source={Source}",
                DiagnosticSource(sourceId, manual));
            return MatchOutcome.ManualReview;
        }

        if (!string.IsNullOrWhiteSpace(confirmation.DedupKey))
        {
            var account = IbanValidator.Normalize(request.Iban);
            var source = SourceFamily(sourceId);
            var material = $"{storeId}\0{source}\0{account}\0{confirmation.DedupKey}";
            var scopedDedup = "sepa:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
            var alreadyUsed = await ctx.SepaPaymentRequests.AnyAsync(r =>
                r.StoreId == storeId && r.Reference != request.Reference
                && (r.DedupKey == scopedDedup
                    || (r.Iban == request.Iban && r.DedupKey == confirmation.DedupKey)), cancellationToken);
            if (alreadyUsed)
                return MatchOutcome.Duplicate;

            request.DedupKey = scopedDedup;
            try
            {
                // Reserve before the external BTCPay call. A competing
                // transaction cannot record a second invoice with this event.
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                await transaction.RollbackAsync(cancellationToken);
                return MatchOutcome.Duplicate;
            }
        }

        var recorded = await _paymentRecorder.RecordAsync(request, confirmation.Amount, cancellationToken);
        if (!recorded)
        {
            // Roll back the reservation and preserve the previous state. The
            // source may retry; the stable BTCPay payment id remains Reference
            // even if an earlier external recording outlived a failed commit.
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("SEPA payment recording did not complete; retry the confirmation.");
        }

        request.State = SepaPaymentRequestState.Confirmed;
        request.ConfirmedAt = DateTimeOffset.UtcNow;
        request.ConfirmedBy = sourceId;
        request.RawConfirmationJson = confirmation.RawJson;
        request.ReviewReason = null;
        await ctx.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "sepa_match_settled source={Source}",
            DiagnosticSource(sourceId, manual));
        return MatchOutcome.Settled;
    }

    private static string SourceFamily(string sourceId)
        => sourceId is "nop-mqtt" or "nop-rest" or "nop-mqtt:catchup" ? "nop" : sourceId;

    private static string DiagnosticSource(string sourceId, bool manual)
        => manual ? "manual" : SourceFamily(sourceId) is "fio" or "nop" or "bmail" ? SourceFamily(sourceId) : "unknown";

    private static string? EvaluateBankEvidence(SepaPaymentRequest request, string sourceId, ConfirmedPayment confirmation)
    {
        var source = SourceFamily(sourceId);
        if (source is not ("fio" or "nop" or "bmail"))
            return "unknown automatic confirmation source";

        // Backend records how the prompt was created. A complementary trusted
        // bank feed may confirm it, but can never replace its receiving account.
        if (!confirmation.BankOriginVerified)
            return "bank origin has not been verified";
        if (!IbanValidator.IsValid(request.Iban))
            return "original receiving account is invalid";

        if (confirmation.CreditorIban is not null
            && (!IbanValidator.IsValid(confirmation.CreditorIban)
                || !string.Equals(IbanValidator.Normalize(confirmation.CreditorIban),
                    IbanValidator.Normalize(request.Iban), StringComparison.Ordinal)))
            return "receiving account does not match the original payment request";

        if (source == "nop")
        {
            var proof = confirmation.NopIntegrity;
            if (proof is null
                || !decimal.TryParse(proof.AmountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rawAmount)
                || rawAmount != confirmation.Amount)
                return "NOP integrity evidence is missing or invalid";

            var expected = NopNotificationProcessor.ComputeHash(request.Iban, proof.AmountRaw,
                confirmation.Currency, confirmation.Reference);
            return string.Equals(expected, proof.DataIntegrityHash, StringComparison.OrdinalIgnoreCase)
                ? null
                : "dataIntegrityHash mismatch";
        }

        return string.IsNullOrWhiteSpace(confirmation.CreditorIban)
            ? "receiving account evidence is missing"
            : null;
    }

    /// <summary>Null = match; otherwise the human-readable mismatch reason.</summary>
    internal static string? Evaluate(
        decimal amountDue,
        string requestCurrency,
        ConfirmedPayment confirmation,
        decimal amountTolerance)
    {
        if (confirmation.IntegrityFailure is not null)
            return confirmation.IntegrityFailure;

        if (!string.Equals(confirmation.Currency, requestCurrency, StringComparison.OrdinalIgnoreCase))
            return $"currency mismatch: expected {requestCurrency}, got {confirmation.Currency}";

        if (confirmation.Amount < amountDue - amountTolerance)
            return $"amount too low: due {amountDue:0.00}, received {confirmation.Amount:0.00}";

        return null;
    }
}
