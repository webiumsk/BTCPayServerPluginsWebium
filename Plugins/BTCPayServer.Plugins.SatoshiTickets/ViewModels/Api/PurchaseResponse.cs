namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class PurchaseResponse
{
    public string OrderId { get; set; }
    public string TxnId { get; set; }
    public string InvoiceId { get; set; }
    public string CheckoutUrl { get; set; }
}
