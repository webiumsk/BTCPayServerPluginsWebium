using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public record CashuMeltSettlementResult(bool Succeeded, string? Error, string? Reference = null);

public interface ICashuMeltSettlementProvider
{
    bool Supports(StoreLightningBackendInfo backendInfo);
    Task<CashuMeltSettlementResult> SettleAsync(
        StoreData store,
        StoreLightningBackendInfo backendInfo,
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest paymentRequest,
        CancellationToken cancellationToken = default);
}
