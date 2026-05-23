namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class CreateTicketsOfflineResponse
{
    public string OrderId { get; set; }
    public string TxnId { get; set; }
    public string OrderReference { get; set; }
    public int TicketsCreated { get; set; }
}
