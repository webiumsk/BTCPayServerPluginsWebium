using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

/// <summary>
/// Parallel-fallback deferral: when the store also offers BTC Lightning (LN or LNURL),
/// the Cashu prompt must wait for checkout activation (no mint quote / settlement record
/// on Lightning-paid invoices). Pure-Cashu stores keep the immediate prompt, and the
/// activation pass itself (invoice already carries our prompt) must never defer.
/// </summary>
public class CashuMeltLazyFallbackTests
{
    private static readonly PaymentMethodId LnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId LnurlId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

    private static StoreData Store(bool withLn = false, bool withLnurl = false, params PaymentMethodId[] excluded)
    {
        var store = new StoreData();
        if (withLn)
            store.SetPaymentMethodConfig(LnId, new JObject
            {
                ["connectionString"] = "type=lnaddress;ln-address=x@example.com;"
            });
        if (withLnurl)
            store.SetPaymentMethodConfig(LnurlId, new JObject());
        if (excluded.Length > 0)
        {
            // Raw blob JSON instead of SetStoreBlob(): serializing a full StoreBlob pulls
            // BTCPayServer runtime-only dependencies the test host does not ship.
            var ids = string.Join(",", System.Linq.Enumerable.Select(excluded, id => $"\"{id}\""));
            store.StoreBlob = $$"""{"excludedPaymentMethods":[{{ids}}]}""";
        }
        return store;
    }

    [Fact]
    public void Defers_when_store_offers_lightning()
        => Assert.True(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store(withLn: true)));

    [Fact]
    public void Defers_for_lnurl_only_stores()
        => Assert.True(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store(withLnurl: true)));

    [Fact]
    public void Defers_when_ln_is_excluded_but_lnurl_still_offered()
        => Assert.True(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(
            Store(withLn: true, withLnurl: true, LnId)));

    [Fact]
    public void Stays_immediate_for_pure_cashu_stores()
        => Assert.False(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(Store()));

    [Fact]
    public void Stays_immediate_when_all_lightning_methods_are_excluded_from_checkout()
        => Assert.False(CashuMeltPaymentMethodHandler.ShouldDeferBehindLightning(
            Store(withLn: true, withLnurl: true, LnId, LnurlId)));

    [Fact]
    public void Defers_only_before_the_invoice_carries_our_prompt()
    {
        var store = Store(withLn: true);

        // Invoice creation: no Cashu prompt on the invoice yet -> defer.
        var fresh = new InvoiceEntity { Currency = "SATS" };
        Assert.True(CashuMeltPaymentMethodHandler.ShouldDeferPrompt(fresh, store));

        // Activation pass: the invoice already carries our (inactive) prompt - the fresh
        // activation context must be allowed to configure it, so no deferral.
        var activating = new InvoiceEntity
        {
            Currency = "SATS",
            PaymentPrompts = new JObject
            {
                [CashuMeltPlugin.CashuMeltPaymentMethodId.ToString()] = new JObject(),
            },
        };
        Assert.False(CashuMeltPaymentMethodHandler.ShouldDeferPrompt(activating, store));
    }
}
