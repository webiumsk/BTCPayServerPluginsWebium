using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.CashuMelt.Data.Entities;

/// <summary>
/// NUT-08 change proof returned by the mint for unused Lightning fee reserve after a melt.
/// Accumulated per store and periodically swept to the merchant Lightning address.
/// </summary>
public class CashuMeltChangeProof
{
    [Key]
    public long Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string StoreId { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string MintUrl { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Unit { get; set; } = "sat";

    public long Amount { get; set; }

    [Required]
    [MaxLength(100)]
    public string KeysetId { get; set; } = string.Empty;

    /// <summary>Proof secret (hex string, UTF-8 bytes are the NUT-00 secret).</summary>
    [Required]
    [MaxLength(200)]
    public string Secret { get; set; } = string.Empty;

    /// <summary>Unblinded signature C (compressed point hex).</summary>
    [Required]
    [MaxLength(66)]
    public string C { get; set; } = string.Empty;

    /// <summary>AVAILABLE | SWEEPING | SWEPT</summary>
    [Required]
    [MaxLength(20)]
    public string State { get; set; } = "AVAILABLE";

    /// <summary>Mint quote id of the payment whose melt produced this change.</summary>
    [MaxLength(100)]
    public string? SourceQuoteId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SweptAt { get; set; }

    /// <summary>Preimage (or melt quote id) of the sweep melt that spent this proof.</summary>
    [MaxLength(200)]
    public string? SweepReference { get; set; }
}
