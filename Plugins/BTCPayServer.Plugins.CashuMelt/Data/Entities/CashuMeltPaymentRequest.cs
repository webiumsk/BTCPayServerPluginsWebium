using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.CashuMelt.Data.Entities;

public class CashuMeltPaymentRequest
{
    [Key]
    [MaxLength(100)]
    public string QuoteId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string InvoiceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string StoreId { get; set; } = string.Empty;

    public long AmountSats { get; set; }

    [MaxLength(20)]
    public string Unit { get; set; } = "sat";

    [MaxLength(500)]
    public string? Bolt11Invoice { get; set; }

    [MaxLength(50)]
    public string State { get; set; } = "UNPAID";

    [MaxLength(50)]
    public string SettlementState { get; set; } = "PENDING";

    [MaxLength(500)]
    public string? SettlementError { get; set; }

    [MaxLength(200)]
    public string? SettlementReference { get; set; }

    // ── Mint+Melt flow fields ──────────────────────────────────────────────────

    /// <summary>
    /// JSON-serialized CashuMeltProof[] minted from the quote.
    /// Populated after successful mint, cleared after successful melt.
    /// Used for crash-recovery: if server restarts between mint and melt,
    /// the stored proofs allow retrying the melt without re-minting.
    /// </summary>
    public string? MintedProofsJson { get; set; }

    /// <summary>The mint's melt quote ID obtained when forwarding to merchant.</summary>
    [MaxLength(200)]
    public string? MeltQuoteId { get; set; }

    /// <summary>
    /// JSON-serialized blinding data (secret + r + keyset) for the NUT-08 blank outputs
    /// sent with the melt. Persisted before the melt POST so change signatures can be
    /// unblinded after a crash (GET melt quote returns them for paid quotes);
    /// cleared once change proofs are stored.
    /// </summary>
    public string? BlankOutputsJson { get; set; }

    /// <summary>The BOLT11 invoice resolved for the merchant's Lightning address.</summary>
    public string? ForwardBolt11 { get; set; }

    // ── Retry tracking ────────────────────────────────────────────────────────────

    /// <summary>Number of background automatic retry attempts. Incremented by the reconciliation service.</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Set to true after <see cref="RetryCount"/> reaches the configured max (20).
    /// Background reconciliation skips this row; only manual retry via UI/API can proceed.
    /// </summary>
    public bool NeedsManualReview { get; set; }

    /// <summary>Machine-readable failure reason code from <c>CashuMeltFailureReasons</c>.</summary>
    [MaxLength(100)]
    public string? FailureReasonCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? SettledAt { get; set; }
}
