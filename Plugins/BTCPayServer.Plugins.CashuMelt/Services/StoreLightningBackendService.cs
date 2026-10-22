using System;
using System.Collections.Generic;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public enum StoreLightningBackendType
{
    Unknown,
    InternalNode,
    Blink,
    Boltz,
    LightningAddressReceiver,
    UnsupportedExternal
}

public record StoreLightningBackendInfo(
    StoreLightningBackendType BackendType,
    string? ConnectionString,
    bool CanAttemptPayout,
    string Description);

public class StoreLightningBackendService(PaymentMethodHandlerDictionary handlers)
{
    private readonly PaymentMethodHandlerDictionary _handlers = handlers;

    public StoreLightningBackendInfo Detect(StoreData store)
    {
        var lnPmId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmId, _handlers);
        if (config is null)
            return new(StoreLightningBackendType.Unknown, null, false,
                store.GetPaymentMethodConfig(PaymentTypes.LNURL.GetPaymentMethodId("BTC")) is not null
                    ? "BTC LNURL is configured; an external payout adapter is not available for this payment method."
                    : "BTC Lightning is not configured for this store.");

        if (config.IsInternalNode)
            return new(StoreLightningBackendType.InternalNode, null, false, "Internal node is configured; external payout adapter is not applicable.");

        var connectionString = config.GetExternalLightningUrl();
        if (string.IsNullOrEmpty(connectionString))
            return new(StoreLightningBackendType.Unknown, null, false, "External lightning connection string is empty.");

        var type = ParseType(connectionString);
        if (type is "lnaddress" or "blitz" or "flash" || (type == "blink" && IsReceiveOnlyAddress(connectionString)))
            return new(StoreLightningBackendType.LightningAddressReceiver, connectionString, false,
                "A receive-only Lightning address wallet is configured. It receives Lightning payments but cannot spend funds for an external payout adapter.");
        return type switch
        {
            "blink" => new(StoreLightningBackendType.Blink, connectionString, true, "Blink backend detected."),
            "boltz" => new(StoreLightningBackendType.Boltz, connectionString, true, "Boltz backend detected."),
            _ => new(StoreLightningBackendType.UnsupportedExternal, connectionString, false, $"Unsupported external backend type '{type ?? "unknown"}'.")
        };
    }

    private static bool IsReceiveOnlyAddress(string connectionString)
    {
        var hasAddress = false;
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            var key = kv[0].ToLowerInvariant();
            if (key is "api-key" or "apikey" or "wallet-id" or "walletid") return false;
            if (key == "currency" && !kv[1].Equals("BTC", StringComparison.OrdinalIgnoreCase)) return false;
            if (key is "ln-address" or "username" or "lnaddress") hasAddress |= !string.IsNullOrWhiteSpace(kv[1]);
        }
        return hasAddress;
    }

    private static string? ParseType(string connectionString)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("type", StringComparison.OrdinalIgnoreCase))
                return kv[1].ToLowerInvariant();
        }
        return null;
    }
}
