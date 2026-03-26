namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

/// <summary>
/// Payment details recorded when a CashuMelt payment completes.
/// </summary>
public class CashuMeltPaymentData
{
    public string QuoteId { get; set; } = string.Empty;
    public long AmountSats { get; set; }
    public string Unit { get; set; } = "sat";
    public string? Bolt11Invoice { get; set; }
}
