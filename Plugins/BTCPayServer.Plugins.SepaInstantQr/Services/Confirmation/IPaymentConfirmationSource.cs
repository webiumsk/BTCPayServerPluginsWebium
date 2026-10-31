using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;

/// <summary>
/// A bank-side confirmation observed by a backend. Backends never settle
/// invoices themselves - they hand these to <see cref="SepaMatchingService"/>.
/// </summary>
/// <param name="Reference">Payment reference (E2E id or variable symbol) as seen by the bank.</param>
/// <param name="Amount">Credited amount.</param>
/// <param name="Currency">ISO 4217 code.</param>
/// <param name="RawJson">Raw notification/transaction payload for audit.</param>
/// <param name="DedupKey">Idempotency key of the delivery; null when the transport has exactly-once semantics.</param>
/// <param name="IntegrityFailure">Non-null when the payload failed integrity verification (e.g. NOP dataIntegrityHash) - routes to manual review, never settles.</param>
/// <param name="CreditorIban">Receiving account observed by the authenticated source, never inferred from store settings.</param>
/// <param name="BankOriginVerified">The adapter authenticated the bank channel; false for legacy/unverified reports.</param>
/// <param name="NopIntegrity">Original NOP hash material, verified against the immutable invoice account by matching.</param>
public record ConfirmedPayment(
    string Reference,
    decimal Amount,
    string Currency,
    string? RawJson,
    string? DedupKey,
    string? IntegrityFailure = null,
    string? CreditorIban = null,
    bool BankOriginVerified = false,
    NopPaymentIntegrity? NopIntegrity = null);

public sealed record NopPaymentIntegrity(string AmountRaw, string DataIntegrityHash);

public record ConfirmationTestResult(bool Ok, string? Message);

/// <summary>
/// Pluggable payment-confirmation backend: manual | fio | nop-mqtt |
/// nop-rest | gocardless. v0.1 ships "manual" only; the interface is the
/// seam the later phases implement.
/// </summary>
public interface IPaymentConfirmationSource
{
    /// <summary>Stable id persisted in store settings.</summary>
    string Id { get; }

    /// <summary>True when a polling hosted service must drive this backend.</summary>
    bool RequiresPolling { get; }

    /// <summary>Settings-page "Test" button implementation.</summary>
    Task<ConfirmationTestResult> TestAsync(SepaStoreSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Optionally acquires the payment reference from the backend (NOP's
    /// generateNewTransactionId). Null → the local generator is used.
    /// </summary>
    Task<string?> AcquireReferenceAsync(SepaStoreSettings settings, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
