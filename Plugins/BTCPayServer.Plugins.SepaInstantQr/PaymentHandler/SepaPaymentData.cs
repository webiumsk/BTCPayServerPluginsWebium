namespace BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;

/// <summary>Payment details recorded when a SEPA transfer is confirmed.</summary>
public class SepaPaymentData
{
    public string Reference { get; set; } = string.Empty;
    public string Iban { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
