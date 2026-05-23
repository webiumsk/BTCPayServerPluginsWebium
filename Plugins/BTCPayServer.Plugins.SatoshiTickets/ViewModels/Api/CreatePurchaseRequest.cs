using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class CreatePurchaseRequest
{
    [Required]
    [MinLength(1)]
    public PurchaseTicketItemRequest[] Tickets { get; set; }
    public string RedirectUrl { get; set; }
    /// <summary>
    /// Optional. When set (e.g. by WooCommerce), the invoice amount will use this value instead of the sum of ticket prices.
    /// Use for orders with coupons/discounts. Must be greater than 0 when specified.
    /// </summary>
    public decimal? OrderTotal { get; set; }
}
