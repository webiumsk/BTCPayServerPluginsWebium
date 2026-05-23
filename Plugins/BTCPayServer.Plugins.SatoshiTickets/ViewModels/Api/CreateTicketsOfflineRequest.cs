using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class CreateTicketsOfflineRequest
{
    [Required]
    [MinLength(1)]
    public PurchaseTicketItemRequest[] Tickets { get; set; }
    /// <summary>
    /// Optional reference (e.g. WooCommerce order ID) for tracking.
    /// </summary>
    public string OrderReference { get; set; }
}
