namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

/// <summary>
/// Details stored in the payment prompt for CashuMelt checkout.
/// </summary>
public class CashuMeltPromptDetails
{
    public string QuoteId { get; set; } = string.Empty;
    public string Bolt11Invoice { get; set; } = string.Empty;
    public long AmountSats { get; set; }
    public string Unit { get; set; } = "sat";
}
