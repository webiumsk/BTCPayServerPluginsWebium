#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

namespace BTCPayServer.Plugins.SepaInstantQr.Models;

/// <summary>
/// Explicit settings ViewModel - the POST binds only these fields (no mass
/// assignment of the settings entity from the request body).
/// </summary>
public class SepaSettingsViewModel : IValidatableObject
{
    public bool Enabled { get; set; }

    [Required]
    [RegularExpression("SK|CZ|EU", ErrorMessage = "Country profile must be SK, CZ or EU.")]
    public string CountryProfile { get; set; } = "SK";

    [Required]
    [MaxLength(42)] // 34 + formatting spaces
    public string Iban { get; set; } = string.Empty;

    [Required]
    [MaxLength(70)]
    public string Beneficiary { get; set; } = string.Empty;

    [MaxLength(11)]
    public string? Bic { get; set; }

    [Required]
    [RegularExpression("payme|bysquare", ErrorMessage = "Unknown SK QR variant.")]
    public string SkQrVariant { get; set; } = "payme";

    [MaxLength(60)]
    public string? Message { get; set; }

    [Required]
    [RegularExpression("manual|fio|nop-mqtt|nop-rest", ErrorMessage = "Unknown confirmation backend.")]
    public string ConfirmationBackend { get; set; } = "manual";

    [Range(typeof(decimal), "0", "10")]
    public decimal AmountTolerance { get; set; }

    /// <summary>Merchant "Mark as paid" button in the checkout - POS only, default off.</summary>
    public bool CheckoutConfirmEnabled { get; set; }

    // ── Fio token (write-only; never rendered back) ──
    [MaxLength(128)]
    public string? FioToken { get; set; }

    public bool ClearFioToken { get; set; }

    /// <summary>Read-only display flag.</summary>
    public bool FioTokenSet { get; set; }

    // ── NOP certificate upload (write-only; secrets never render back) ──
    [RegularExpression("INT|PROD")]
    public string NopEnvironment { get; set; } = "INT";

    /// <summary>eKasa certificate: PEM certificate file.</summary>
    public Microsoft.AspNetCore.Http.IFormFile? NopCertPemFile { get; set; }

    /// <summary>eKasa certificate: PEM private key file (with NopCertPemFile).</summary>
    public Microsoft.AspNetCore.Http.IFormFile? NopKeyPemFile { get; set; }

    /// <summary>eKasa certificate: PKCS#12 (.p12/.pfx) alternative.</summary>
    public Microsoft.AspNetCore.Http.IFormFile? NopPfxFile { get; set; }

    [MaxLength(200)]
    public string? NopPfxPassword { get; set; }

    public bool ClearNopCertificate { get; set; }

    // ── Read-only display state (populated by the controller) ──
    public bool NopCertSet { get; set; }
    public string? NopVatsk { get; set; }
    public string? NopPokladnica { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Services.IbanValidator.IsValid(Iban))
            yield return new ValidationResult("The IBAN is not valid (checksum failed).", [nameof(Iban)]);

        var hasPemPair = NopCertPemFile is not null && NopKeyPemFile is not null;
        var hasHalfPem = (NopCertPemFile is null) != (NopKeyPemFile is null);
        if (hasHalfPem)
            yield return new ValidationResult(
                "Upload the PEM certificate together with its private key.", [nameof(NopCertPemFile)]);
        if (hasPemPair && NopPfxFile is not null)
            yield return new ValidationResult(
                "Upload either the PEM pair or the PKCS#12 file, not both.", [nameof(NopPfxFile)]);
    }
}

public class SepaPendingRowViewModel
{
    public string Reference { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string State { get; set; } = SepaPaymentRequestState.Pending;
    public decimal AmountDue { get; set; }
    public string Currency { get; set; } = "EUR";
    public System.DateTimeOffset CreatedAt { get; set; }
    public string? ReviewReason { get; set; }
}

public class SepaSettingsPageViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public SepaSettingsViewModel Settings { get; set; } = new();
    public List<SepaPendingRowViewModel> PendingRequests { get; set; } = [];
    public List<SepaPendingRowViewModel> ReviewRequests { get; set; } = [];
    public string? TestResultMessage { get; set; }
    public bool? TestResultOk { get; set; }
}
