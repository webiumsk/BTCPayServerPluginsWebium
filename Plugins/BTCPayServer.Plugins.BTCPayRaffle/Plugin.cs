#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.BTCPayRaffle.Data;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BTCPayRaffle;

public class BTCPayRafflePlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // ── Database ─────────────────────────────────────────────────────────
        services.AddSingleton<RaffleDbContextFactory>();
        services.AddDbContext<RaffleDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<RaffleDbContextFactory>();
            factory.ConfigureBuilder(o);
        }, ServiceLifetime.Transient);
        services.AddStartupTask<PluginMigrationRunner>();

        // ── Services ─────────────────────────────────────────────────────────
        services.AddSingleton<RaffleService>();
        services.AddSingleton<RafflePresenterTokenService>();
        services.AddHostedService<RaffleInvoiceWatcher>();

        // ── UI extensions (injected into BTCPay layout slots) ─────────────────
        services.AddUIExtension("store-integrations-nav", "BTCPayRaffle/StoreNavExtension");

        base.Execute(services);
    }
}
