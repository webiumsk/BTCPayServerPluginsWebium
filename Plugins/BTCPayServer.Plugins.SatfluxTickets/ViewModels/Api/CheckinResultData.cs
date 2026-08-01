namespace BTCPayServer.Plugins.SatfluxTickets.Models.Api;

public class CheckinResultData
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public TicketData Ticket { get; set; }
}
