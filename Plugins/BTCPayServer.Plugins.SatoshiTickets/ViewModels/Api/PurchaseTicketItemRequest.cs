using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class PurchaseTicketItemRequest
{
    [Required]
    public string TicketTypeId { get; set; }
    [Required]
    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
    [Required]
    public RecipientRequest[] Recipients { get; set; }
}
