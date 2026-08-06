#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Flash;

public class FlashPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        // All plugin outbound HTTP goes through this named client: redirects disabled, every
        // connect DNS-filtered against private/loopback/reserved ranges (SSRF/rebinding guard).
        services.AddHttpClient(FlashHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(FlashHttp.CreateSafeHandler);
        services.AddUIExtension("ln-payment-method-setup-tab", "Flash/LNPaymentMethodSetupTab");
        services.AddSingleton<FlashConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(sp =>
            sp.GetRequiredService<FlashConnectionStringHandler>());
        services.AddSingleton<FlashPollerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FlashPollerService>());
        services.AddSingleton<IPluginHookFilter, FlashLnurlRequestFilter>();
        base.Execute(services);
    }
}
