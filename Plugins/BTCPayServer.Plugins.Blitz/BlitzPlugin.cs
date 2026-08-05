#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Blitz;

public class BlitzPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        // All plugin outbound HTTP goes through this named client: redirects disabled, every
        // connect DNS-filtered against private/loopback/reserved ranges (SSRF/rebinding guard).
        services.AddHttpClient(BlitzHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(BlitzHttp.CreateSafeHandler);
        services.AddUIExtension("ln-payment-method-setup-tab", "Blitz/LNPaymentMethodSetupTab");
        services.AddSingleton<BlitzConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(sp =>
            sp.GetRequiredService<BlitzConnectionStringHandler>());
        services.AddSingleton<BlitzPollerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BlitzPollerService>());
        services.AddSingleton<IPluginHookFilter, BlitzLnurlRequestFilter>();
        base.Execute(services);
    }
}
