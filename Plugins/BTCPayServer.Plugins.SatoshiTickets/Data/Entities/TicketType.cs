using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.SatoshiTickets.Data;

public class TicketType
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; }
    public bool IsDefault { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
    public string Description { get; set; }
    public int Quantity { get; set; }
    public int QuantitySold { get; set; }
    public string EventId { get; set; }
    public EntityState TicketTypeState { get; set; }
    /// <summary>BTCPay Raffle id when this ticket type includes raffle entries.</summary>
    public Guid? BundledRaffleId { get; set; }
    /// <summary>Raffle tickets granted per one ticket of this type (same buyer email).</summary>
    public int BundledRaffleTicketsPerAdmission { get; set; }
}
