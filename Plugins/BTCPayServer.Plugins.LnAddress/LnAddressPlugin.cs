#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.LnAddress;

public class LnAddressPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        // All plugin outbound HTTP goes through this named client: redirects disabled, every
        // connect DNS-filtered against private/loopback/reserved ranges (SSRF/rebinding guard).
        services.AddHttpClient(LnAddressHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(LnAddressHttp.CreateSafeHandler);
        services.AddUIExtension("ln-payment-method-setup-tab", "LnAddress/LNPaymentMethodSetupTab");
        services.AddSingleton<LnAddressConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(sp =>
            sp.GetRequiredService<LnAddressConnectionStringHandler>());
        services.AddSingleton<LnAddressPollerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LnAddressPollerService>());
        services.AddSingleton<IPluginHookFilter, LnAddressLnurlRequestFilter>();
        base.Execute(services);
    }
}
