#nullable enable
using System;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

public class RaffleDrawing
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RaffleId { get; set; }
    public Raffle Raffle { get; set; } = null!;

    /// <summary>
    /// Sequential draw counter (1 = first draw = last prize, N = last draw = first prize).
    /// Higher DrawOrder means more valuable prize — the final draw is always the grand prize.
    /// </summary>
    public int DrawOrder { get; set; }

    public Guid WinningTicketId { get; set; }
    public RaffleTicket WinningTicket { get; set; } = null!;

    public DateTimeOffset DrawnAt { get; set; } = DateTimeOffset.UtcNow;
}
