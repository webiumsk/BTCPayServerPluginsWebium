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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
