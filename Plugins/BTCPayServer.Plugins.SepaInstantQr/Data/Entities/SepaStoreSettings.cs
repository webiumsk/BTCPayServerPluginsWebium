using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

/// <summary>
/// Per-store configuration. The merchant's own IBAN is the payment
/// destination - the plugin never takes custody of funds.
/// </summary>
public class SepaStoreSettings
{
    [Key]
    [MaxLength(100)]
    public string StoreId { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Country profile driving the QR format: SK | CZ | EU.</summary>
    [Required]
    [MaxLength(2)]
    public string CountryProfile { get; set; } = "SK";

    [Required]
    [MaxLength(34)]
    public string Iban { get; set; } = string.Empty;

    /// <summary>Beneficiary (creditor) name shown to the payer; max 70 per the QR standards.</summary>
    [Required]
    [MaxLength(70)]
    public string Beneficiary { get; set; } = string.Empty;

    [MaxLength(11)]
    public string? Bic { get; set; }

    /// <summary>SK profile QR variant: payme (PayMe link, NOP-recommended) | bysquare.</summary>
    [Required]
    [MaxLength(16)]
    public string SkQrVariant { get; set; } = "payme";

    /// <summary>Optional message/remittance shown to the payer (business name + branch recommended).</summary>
    [MaxLength(60)]
    public string? Message { get; set; }

    /// <summary>Active confirmation backend id: manual | fio | nop-mqtt | nop-rest | gocardless.</summary>
    [Required]
    [MaxLength(20)]
    public string ConfirmationBackend { get; set; } = "manual";

    /// <summary>
    /// Merchant-facing "Mark as paid" button directly in the checkout.
    /// Default OFF and deliberately opt-in: the checkout page runs on the
    /// customer's device in e-commerce, where anyone could press it. Enable
    /// only for counter-top POS devices the merchant controls.
    /// </summary>
    public bool CheckoutConfirmEnabled { get; set; }

    /// <summary>
    /// Amount tolerance in EUR for automated matching (0 = exact). A payment
    /// below due - tolerance never auto-settles; it lands in manual review.
    /// </summary>
    public decimal AmountTolerance { get; set; }

    /// <summary>
    /// Backend credentials as a data-protected JSON blob (NOP certificate,
    /// aggregator secrets). Encrypted at rest, never logged.
    /// </summary>
    public string? EncryptedCredentialsJson { get; set; }

    /// <summary>NOP identity cached from the uploaded eKasa certificate ("VATSK-1234567890").</summary>
    [MaxLength(20)]
    public string? NopVatsk { get; set; }

    /// <summary>Cash-register code from the certificate CN (without the "POKLADNICA-" prefix).</summary>
    [MaxLength(30)]
    public string? NopPokladnica { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
