using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SatfluxTickets.Models.Api;

public sealed class TicketTypeOrderRequest
{
    [Required, MaxLength(1000)]
    public string[] TicketTypeIds { get; set; }
    [Required, Range(typeof(long), "0", "9007199254740991")]
    public long? ExpectedRevision { get; set; }
}

public sealed class TicketTypeOrderSnapshot
{
    public string[] TicketTypeIds { get; set; }
    public long Revision { get; set; }
}
