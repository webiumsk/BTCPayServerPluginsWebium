#nullable enable
using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleEventBundleResult
{
    public bool Success { get; init; }
    public bool IsNew { get; init; }
    public int TicketsAllocated { get; init; }
    public string? Error { get; init; }

    public static RaffleEventBundleResult Ok(int count, bool isNew) => new()
    {
        Success = true,
        IsNew = isNew,
        TicketsAllocated = count
    };

    public static RaffleEventBundleResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };

    public static RaffleEventBundleResult Skipped() => new()
    {
        Success = true,
        IsNew = false,
        TicketsAllocated = 0
    };
}

public interface IRaffleEventBundleService
{
    /// <summary>Validates raffle exists, belongs to store, and is Open (for event configuration).</summary>
    Task<(bool Ok, string? Error)> ValidateBundledRaffleAsync(string storeId, Guid raffleId);

    /// <summary>
    /// Idempotent bundle allocation for one buyer email on an event order.
    /// </summary>
    Task<RaffleEventBundleResult> AllocateForEventOrderAsync(
        string storeId,
        Guid raffleId,
        int count,
        string buyerEmail,
        string? buyerName,
        string eventOrderId,
        string baseUrl);
}
