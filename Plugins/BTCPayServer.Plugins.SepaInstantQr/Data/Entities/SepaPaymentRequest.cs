using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

public static class SepaPaymentRequestState
{
    public const string Pending = "PENDING";
    public const string Confirmed = "CONFIRMED";
    public const string ManualReview = "MANUAL_REVIEW";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// One row per invoice payment prompt: the unique payment reference the
/// payer's transfer must carry, and its confirmation state. The reference is
/// the matching key for every confirmation backend.
/// </summary>
public class SepaPaymentRequest
{
    /// <summary>
    /// SK/EU: NOP-shaped end-to-end id "QR-" + 32 lowercase hex.
    /// CZ: numeric variable symbol (1-10 digits).
    /// </summary>
    [Key]
    [MaxLength(35)]
    public string Reference { get; set; } = string.Empty;

    /// <summary>E2E (end-to-end id) or VS (variable symbol).</summary>
    [Required]
    [MaxLength(3)]
    public string ReferenceKind { get; set; } = "E2E";

    [Required]
    [MaxLength(100)]
    public string InvoiceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>Confirmation backend active when the prompt was created.</summary>
    [Required]
    [MaxLength(20)]
    public string Backend { get; set; } = "manual";

    [Required]
    [MaxLength(20)]
    public string State { get; set; } = SepaPaymentRequestState.Pending;

    public decimal AmountDue { get; set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    [Required]
    [MaxLength(34)]
    public string Iban { get; set; } = string.Empty;

    /// <summary>The exact QR payload rendered at checkout (for audit/re-display).</summary>
    [MaxLength(500)]
    public string QrPayload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>User id for manual confirmation, backend id otherwise.</summary>
    [MaxLength(100)]
    public string? ConfirmedBy { get; set; }

    /// <summary>Raw backend notification/transaction JSON kept for audit.</summary>
    public string? RawConfirmationJson { get; set; }

    /// <summary>Reason a confirmation landed in MANUAL_REVIEW (amount/currency mismatch).</summary>
    [MaxLength(200)]
    public string? ReviewReason { get; set; }

    /// <summary>
    /// Idempotency key of the confirming notification (e.g. NOP endToEndId +
    /// receivedAt). Unique when present - duplicate deliveries are dropped.
    /// </summary>
    [MaxLength(120)]
    public string? DedupKey { get; set; }
}
