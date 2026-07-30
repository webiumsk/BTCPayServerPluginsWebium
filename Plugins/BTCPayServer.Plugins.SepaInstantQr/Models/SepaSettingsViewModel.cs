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

    [MaxLength(60)]
    public string? Message { get; set; }

    [Required]
    [RegularExpression("manual", ErrorMessage = "Unknown confirmation backend.")]
    public string ConfirmationBackend { get; set; } = "manual";

    [Range(0, 10)]
    public decimal AmountTolerance { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Services.IbanValidator.IsValid(Iban))
            yield return new ValidationResult("The IBAN is not valid (checksum failed).", [nameof(Iban)]);
    }
}

public class SepaPendingRowViewModel
{
    public string Reference { get; set; } = string.Empty;
    public string InvoiceId { get; set; } = string.Empty;
    public string State { get; set; } = SepaPaymentRequestState.Pending;
    public decimal AmountDue { get; set; }
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
