#nullable enable
using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SatoshiTickets.Services.Integration;

/// <summary>
/// Local contract for optional BTCPay Raffle plugin integration (no compile-time assembly reference).
/// </summary>
public interface IRaffleEventBundleClient
{
    Task<(bool Ok, string? Error)> ValidateBundledRaffleAsync(string storeId, Guid raffleId);

    Task<RaffleBundleAllocationResult> AllocateForEventOrderAsync(
        string storeId,
        Guid raffleId,
        int count,
        string buyerEmail,
        string? buyerName,
        string eventOrderId,
        string baseUrl);
}

public sealed class RaffleBundleAllocationResult
{
    public bool Success { get; init; }
    public bool IsNew { get; init; }
    public int TicketsAllocated { get; init; }
    public string? Error { get; init; }
}
