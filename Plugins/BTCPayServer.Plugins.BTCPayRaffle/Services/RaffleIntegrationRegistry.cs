#nullable enable

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Cross-plugin discovery for Satoshi Tickets (avoids DI type identity issues across plugin load contexts).
/// </summary>
public static class RaffleIntegrationRegistry
{
    public static IRaffleEventBundleService? EventBundleService { get; set; }
}
