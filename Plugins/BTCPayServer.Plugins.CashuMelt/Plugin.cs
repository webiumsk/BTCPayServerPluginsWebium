#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.CashuMelt;

public class CashuMeltPlugin : BaseBTCPayServerPlugin
{
    public static readonly PaymentMethodId CashuMeltPaymentMethodId = new("CASHU");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // ── Database ────────────────────────────────────────────────────────────
        services.AddSingleton<CashuMeltDbContextFactory>();
        services.AddDbContext<CashuMeltDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<CashuMeltDbContextFactory>();
            factory.ConfigureBuilder(o);
        }, ServiceLifetime.Transient);
        services.AddStartupTask<PluginMigrationRunner>();

        // ── HTTP clients ────────────────────────────────────────────────────────
        services.AddHttpClient<CashuMeltMintClient>();
        // LightningAddressResolver is instantiated directly via IHttpClientFactory in CashuMeltPaymentService
        services.AddHttpClient(nameof(LightningAddressResolver));

        // ── Services ─────────────────────────────────────────────────────────
        services.AddSingleton<CashuMeltConfigService>();
        services.AddSingleton<CashuMeltLightningAddressValidator>();
        services.AddSingleton<StoreLightningBackendService>();
        services.AddTransient<CashuMeltPaymentService>();
        services.AddHostedService<CashuMeltReconciliationHostedService>();

        // ── BTCPay payment method integration ──────────────────────────────────
        services.AddSingleton<IPaymentMethodHandler, CashuMeltPaymentMethodHandler>();
        services.AddSingleton<ICheckoutModelExtension, CashuMeltCheckoutModelExtension>();
        services.AddTransactionLinkProvider(CashuMeltPaymentMethodId, new CashuMeltTransactionLinkProvider());

        // ── UI extensions (injected into BTCPay layout slots) ──────────────────
        services.AddUIExtension("store-wallets-nav",      "CashuMelt/StoreNavExtension");
        services.AddUIExtension("checkout-payment",       "CashuMelt/CashuPreferLightningRedirect");
        services.AddUIExtension("checkout-end",           "CashuMelt/CashuMeltCheckoutExtension");
        services.AddUIExtension("store-invoices-payments","CashuMelt/ViewCashuMeltPaymentData");

        services.AddDefaultPrettyName(CashuMeltPaymentMethodId, "Cashu Melt");

        base.Execute(services);
    }
}
