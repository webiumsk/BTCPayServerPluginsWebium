using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public class ExternalLightningSettlementProvider(
    BTCPayNetworkProvider networkProvider,
    LightningClientFactoryService lightningClientFactory,
    LightningAddressResolver lightningAddressResolver,
    ILogger<ExternalLightningSettlementProvider> logger) : ICashuMeltSettlementProvider
{
    private readonly BTCPayNetworkProvider _networkProvider = networkProvider;
    private readonly LightningClientFactoryService _lightningClientFactory = lightningClientFactory;
    private readonly LightningAddressResolver _lightningAddressResolver = lightningAddressResolver;
    private readonly ILogger<ExternalLightningSettlementProvider> _logger = logger;

    public bool Supports(StoreLightningBackendInfo backendInfo)
    {
        return backendInfo.BackendType is StoreLightningBackendType.Blink or StoreLightningBackendType.Boltz;
    }

    public async Task<CashuMeltSettlementResult> SettleAsync(
        StoreData store,
        StoreLightningBackendInfo backendInfo,
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest paymentRequest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.LightningAddress))
            return new(false, "Lightning address is missing for payout.");
        if (string.IsNullOrWhiteSpace(backendInfo.ConnectionString))
            return new(false, "Store Lightning connection string is missing.");

        var (bolt11, _) = await _lightningAddressResolver.ResolveInvoiceAsync(
            settings.LightningAddress.Trim(),
            paymentRequest.AmountSats,
            paymentRequest.AmountSats,
            cancellationToken);

        var btcNetwork = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var client = _lightningClientFactory.Create(backendInfo.ConnectionString, btcNetwork);

        var payResult = await client.Pay(bolt11, cancellation: cancellationToken);
        if (payResult.Result is not PayResult.Ok)
        {
            var error = payResult.ErrorDetail ?? $"Payout failed with status {payResult.Result}.";
            _logger.LogWarning("CashuMelt settlement payout failed for invoice {InvoiceId}: {Error}", paymentRequest.InvoiceId, error);
            return new(false, error);
        }

        var reference = payResult.Details?.PaymentHash?.ToString();
        _logger.LogInformation(
            "CashuMelt payout settled for invoice {InvoiceId} via {Backend}. PaymentHash={PaymentHash}",
            paymentRequest.InvoiceId,
            backendInfo.BackendType,
            reference);

        return new(true, null, reference);
    }
}
