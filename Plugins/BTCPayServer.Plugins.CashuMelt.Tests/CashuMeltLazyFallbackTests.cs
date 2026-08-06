using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

/// <summary>
/// Parallel-fallback deferral: when the store also offers BTC Lightning, the Cashu
/// prompt must wait for checkout activation (no mint quote / settlement record on
/// Lightning-paid invoices). Pure-Cashu stores keep the immediate prompt.
/// </summary>
public class CashuMeltLazyFallbackTests
{
    private static readonly PaymentMethodId LnId = PaymentTypes.LN.GetPaymentMethodId("BTC");

    private static StoreData Store(bool withLightning, bool lightningExcluded = false)
    {
        var store = new StoreData();
        if (withLightning)
            store.SetPaymentMethodConfig(LnId, new JObject
            {
                ["connectionString"] = "type=lnaddress;ln-address=x@example.com;"
            });
        if (lightningExcluded)
        {
            // Raw blob JSON instead of SetStoreBlob(): serializing a full StoreBlob pulls
            // BTCPayServer runtime-only dependencies the test host does not ship.
            store.StoreBlob = """{"excludedPaymentMethods":["BTC-LN"]}""";
        }
        return store;
    }

    [Fact]
    public void Defers_when_store_offers_lightning()
        => Assert.True(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store(withLightning: true)));

    [Fact]
    public void Stays_immediate_for_pure_cashu_stores()
        => Assert.False(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store(withLightning: false)));

    [Fact]
    public void Stays_immediate_when_lightning_is_excluded_from_checkout()
        => Assert.False(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store(withLightning: true, lightningExcluded: true)));
}
