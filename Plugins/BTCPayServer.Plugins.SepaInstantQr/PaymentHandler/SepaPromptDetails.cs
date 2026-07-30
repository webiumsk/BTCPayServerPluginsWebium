namespace BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;

/// <summary>Details stored in the payment prompt for the SEPA checkout tab.</summary>
public class SepaPromptDetails
{
    public string Reference { get; set; } = string.Empty;
    public string QrPayload { get; set; } = string.Empty;
    public string Iban { get; set; } = string.Empty;
    public string Beneficiary { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string CountryProfile { get; set; } = "SK";
}
