#nullable enable
using System.Threading;

namespace BTCPayServer.Plugins.SatoshiTickets.Services.Integration;

/// <summary>
/// Holds Raffle bundle service instance registered by BTCPay Raffle at startup (cross-ALC bridge).
/// </summary>
public static class SatoshiTicketsRaffleBridge
{
    private static object? _eventBundleService;

    public static void RegisterEventBundleService(object service)
    {
        Volatile.Write(ref _eventBundleService, service);
    }

    public static object? EventBundleService => Volatile.Read(ref _eventBundleService);
}
