#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using BTCPayServer.Plugins.SepaInstantQr.Services.Qr;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SepaInstantQr;

public class SepaInstantQrPlugin : BaseBTCPayServerPlugin
{
    public static readonly PaymentMethodId SepaPaymentMethodId = new("SEPA_INSTANT");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // ── Database ───────────────────────────────────────────────────────
        services.AddSingleton<SepaDbContextFactory>();
        services.AddDbContext<SepaDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<SepaDbContextFactory>();
            factory.ConfigureBuilder(o);
        }, ServiceLifetime.Transient);
        services.AddStartupTask<PluginMigrationRunner>();

        // ── Services ───────────────────────────────────────────────────────
        services.AddSingleton<SepaConfigService>();
        services.AddSingleton<SepaPaymentRecorder>();
        services.AddSingleton<SepaMatchingService>();

        // QR payload builders (profile-keyed)
        services.AddSingleton<IQrPayloadBuilder, PayMeV2PayloadBuilder>();
        services.AddSingleton<IQrPayloadBuilder, PayBySquarePayloadBuilder>();
        services.AddSingleton<IQrPayloadBuilder, SpdPayloadBuilder>();
        services.AddSingleton<IQrPayloadBuilder, EpcQrPayloadBuilder>();

        // Confirmation backends: manual + Slovak NOP (MQTT push, REST poll)
        services.AddSingleton<IPaymentConfirmationSource, ManualConfirmSource>();
        services.AddSingleton<NopNotificationProcessor>();
        services.AddSingleton<IPaymentConfirmationSource, NopMqttSource>();
        services.AddSingleton<IPaymentConfirmationSource, NopRestPollerSource>();
        services.AddHostedService<NopMqttListener>();
        services.AddHostedService<SepaPollingHostedService>();

        // ── BTCPay payment method integration ──────────────────────────────
        services.AddSingleton<IPaymentMethodHandler, SepaPaymentMethodHandler>();
        services.AddSingleton<ICheckoutModelExtension, SepaCheckoutModelExtension>();
        services.AddTransactionLinkProvider(SepaPaymentMethodId, new SepaTransactionLinkProvider());
        services.AddDefaultPrettyName(SepaPaymentMethodId, "SEPA Instant QR");

        // ── UI extensions ──────────────────────────────────────────────────
        services.AddUIExtension("store-wallets-nav", "SepaInstantQr/StoreNavExtension");
        services.AddUIExtension("checkout-end", "SepaInstantQr/SepaCheckoutExtension");
        services.AddUIExtension("store-invoices-payments", "SepaInstantQr/ViewSepaPaymentData");

        base.Execute(services);
    }
}
