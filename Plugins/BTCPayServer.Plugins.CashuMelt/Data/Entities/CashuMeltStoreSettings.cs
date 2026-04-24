using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.CashuMelt.Data.Entities;

public class CashuMeltStoreSettings
{
    [Key]
    [MaxLength(100)]
    public string StoreId { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string MintUrl { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Unit { get; set; } = "sat";

    [MaxLength(500)]
    public string? LightningAddress { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional newline- or comma-separated HTTPS mint base URLs. When empty, only <see cref="MintUrl"/> is allowed.
    /// When set, <see cref="MintUrl"/> must match one entry (after normalization). Does not store customer funds;
    /// it is an operator allow-list for which mint origins may be used.
    /// </summary>
    public string? TrustedMintUrls { get; set; }

    /// <summary>Optional hard cap on melt Lightning fee reserve (satoshis), quoted by the mint.</summary>
    public long? MaxMeltFeeReserveSats { get; set; }

    /// <summary>Optional cap: max fee reserve as a percent of total minted amount (0–100).</summary>
    public decimal? MaxMeltFeeReservePercentOfMinted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
