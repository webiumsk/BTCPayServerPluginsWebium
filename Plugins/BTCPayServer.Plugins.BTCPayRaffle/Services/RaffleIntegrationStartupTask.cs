#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleIntegrationStartupTask(IRaffleEventBundleService bundleService) : IStartupTask
{
    private const string SatoshiBridgeTypeName =
        "BTCPayServer.Plugins.SatoshiTickets.Services.Integration.SatoshiTicketsRaffleBridge";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        RaffleIntegrationRegistry.EventBundleService = bundleService;
        RegisterSatoshiBridge(bundleService);
        return Task.CompletedTask;
    }

    private static void RegisterSatoshiBridge(IRaffleEventBundleService bundleService)
    {
        // The Webium fork ships under its own plugin identifier (assembly
        // name) so upstream updates are never offered - accept both so the
        // bridge works regardless of which build is installed.
        string[] satoshiAssemblyNames =
        [
            "BTCPayServer.Plugins.SatoshiTicketsWebium",
            "BTCPayServer.Plugins.SatoshiTickets",
        ];
        var satoshiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => satoshiAssemblyNames.Contains(a.GetName().Name, StringComparer.Ordinal));
        if (satoshiAssembly is null)
            return;

        var bridgeType = satoshiAssembly.GetType(SatoshiBridgeTypeName, throwOnError: false);
        var register = bridgeType?.GetMethod(
            "RegisterEventBundleService",
            BindingFlags.Public | BindingFlags.Static,
            null,
            [typeof(object)],
            null);
        register?.Invoke(null, [bundleService]);
    }
}
