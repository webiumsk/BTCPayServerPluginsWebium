#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SepaInstantQr.Models;

/// <summary>Settings snapshot returned by the Greenfield API. Secrets never leave the server - only *Set flags and the parsed identity.</summary>
public class SepaSettingsData
{
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public string CountryProfile { get; set; } = "SK";
    public string Iban { get; set; } = string.Empty;
    public string Beneficiary { get; set; } = string.Empty;
    public string? Bic { get; set; }
    public string? Message { get; set; }
    public string ConfirmationBackend { get; set; } = "manual";
    public string SkQrVariant { get; set; } = "payme";
    public decimal AmountTolerance { get; set; }
    public string NopEnvironment { get; set; } = "INT";
    public bool NopCertSet { get; set; }
    public bool FioTokenSet { get; set; }
    public bool CheckoutConfirmEnabled { get; set; }
    public string? NopVatsk { get; set; }
    public string? NopPokladnica { get; set; }
}

public class SepaUpdateSettingsRequest
{
    public bool Enabled { get; set; }

    [Required]
    [RegularExpression("SK|CZ|EU", ErrorMessage = "Country profile must be SK, CZ or EU.")]
    public string CountryProfile { get; set; } = "SK";

    [Required]
    [MaxLength(42)]
    public string Iban { get; set; } = string.Empty;

    [Required]
    [MaxLength(70)]
    public string Beneficiary { get; set; } = string.Empty;

    [MaxLength(11)]
    public string? Bic { get; set; }

    [MaxLength(60)]
    public string? Message { get; set; }

    [Required]
    [RegularExpression("manual|fio|nop-mqtt|nop-rest", ErrorMessage = "Unknown confirmation backend.")]
    public string ConfirmationBackend { get; set; } = "manual";

    [Required]
    [RegularExpression("payme|bysquare", ErrorMessage = "Unknown SK QR variant.")]
    public string SkQrVariant { get; set; } = "payme";

    [Range(typeof(decimal), "0", "10")]
    public decimal AmountTolerance { get; set; }

    /// <summary>Merchant "Mark as paid" button in the checkout - POS only, default off.</summary>
    public bool CheckoutConfirmEnabled { get; set; }

    /// <summary>Optional - omitting it keeps the currently stored NOP environment.</summary>
    [RegularExpression("INT|PROD")]
    public string? NopEnvironment { get; set; }
}

/// <summary>Certificate upload: either PfxBase64 (+PfxPassword) or the CertPem/KeyPem pair.</summary>
public class SepaUploadCertificateRequest
{
    public string? PfxBase64 { get; set; }
    public string? PfxPassword { get; set; }
    public string? CertPem { get; set; }
    public string? KeyPem { get; set; }

    /// <summary>Optional - omitting it keeps the currently stored NOP environment.</summary>
    [RegularExpression("INT|PROD")]
    public string? NopEnvironment { get; set; }
}

/// <summary>Write-only Fio token upload. Fio tokens are exactly 64
/// characters (API Bankovnictví v1.9) - the service trims before the
/// length check, so surrounding whitespace is tolerated.</summary>
public class SepaFioTokenRequest
{
    [Required]
    [MaxLength(128)]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Amount-verified external confirmation (satflux b-mail channel): unlike
/// the manual confirm endpoint, the reported amount/currency must match the
/// pending request (within the store tolerance) or the payment lands in
/// manual review instead of settling.
/// </summary>
public class SepaReportPaymentRequest
{
    [Required]
    [MaxLength(35)]
    [RegularExpression("[A-Za-z0-9_-]+")]
    public string Reference { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
    public decimal Amount { get; set; }

    [Required]
    [MaxLength(8)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>Optional idempotency key of the delivery (e.g. inbound mail id).</summary>
    [MaxLength(120)]
    public string? DedupKey { get; set; }
}

public class SepaPaymentRequestData
{
    public string Reference { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public decimal AmountDue { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReviewReason { get; set; }
}

public class SepaTestResultData
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
}
