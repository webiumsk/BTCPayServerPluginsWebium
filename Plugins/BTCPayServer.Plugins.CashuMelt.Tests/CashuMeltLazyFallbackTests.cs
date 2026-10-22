using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

/// <summary>Any configured Lightning wallet excludes new Cashu checkout, independently of readiness or exclusion.</summary>
public class CashuMeltLazyFallbackTests
{
    private static readonly PaymentMethodId LnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId LnurlId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

    internal static StoreData Store(bool withLn = false, bool withLnurl = false, params PaymentMethodId[] excluded)
    {
        var store = new StoreData { Id = "store" };
        if (withLn) store.SetPaymentMethodConfig(LnId, new JObject { ["connectionString"] = "type=lnaddress;ln-address=x@example.com;" });
        if (withLnurl) store.SetPaymentMethodConfig(LnurlId, new JObject());
        if (excluded.Length > 0)
        {
            var ids = string.Join(",", System.Linq.Enumerable.Select(excluded, id => $"\"{id}\""));
            store.StoreBlob = $$"""{"excludedPaymentMethods":[{{ids}}]}""";
        }
        return store;
    }

    [Fact]
    public void Any_configured_Lightning_wallet_counts_even_when_all_methods_are_excluded()
    {
        Assert.True(CashuMeltPaymentMethodHandler.HasConfiguredLightningWallet(Store(withLn: true)));
        Assert.True(CashuMeltPaymentMethodHandler.HasConfiguredLightningWallet(Store(withLnurl: true)));
        Assert.True(CashuMeltPaymentMethodHandler.HasConfiguredLightningWallet(Store(withLn: true, withLnurl: true, LnId, LnurlId)));
        Assert.False(CashuMeltPaymentMethodHandler.HasConfiguredLightningWallet(Store()));
    }

    [Theory]
    [InlineData("type=lnaddress;ln-address=x@example.com")]
    [InlineData("type=blink;api-key=synthetic;wallet-id=synthetic")]
    [InlineData("type=boltz;server=https://unavailable.invalid")]
    [InlineData("type=lnd-rest;server=https://unavailable.invalid")]
    [InlineData("")]
    public async Task New_checkout_is_suppressed_without_accessing_settings_or_mint_even_for_unknown_or_failing_wallets(string connection)
    {
        var store = Store(); store.SetPaymentMethodConfig(LnId, new JObject { ["connectionString"] = connection });
        await AssertSuppressed(store, new InvoiceEntity { Id = "new", StoreId = store.Id, Currency = "BTC" });
    }

    [Fact]
    public async Task Legacy_inactive_placeholder_cannot_activate_a_new_quote_behind_configured_Lightning()
    {
        var store = Store(withLn: true, excluded: [LnId]);
        var invoice = new InvoiceEntity { Id = "legacy", StoreId = store.Id, Currency = "BTC" };
        invoice.SetPaymentPrompt(CashuMeltPlugin.CashuMeltPaymentMethodId, new PaymentPrompt { PaymentMethodId = CashuMeltPlugin.CashuMeltPaymentMethodId, Inactive = true });
        await AssertSuppressed(store, invoice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Partial_materialization_hint_is_not_treated_as_a_fresh_placeholder_even_without_Lightning(string? destination)
    {
        var store = Store();
        var invoice = new InvoiceEntity { Id = "partial", StoreId = store.Id, Currency = "BTC" };
        invoice.SetPaymentPrompt(CashuMeltPlugin.CashuMeltPaymentMethodId, new PaymentPrompt {
            PaymentMethodId = CashuMeltPlugin.CashuMeltPaymentMethodId, Destination = destination,
            Details = new JObject { ["quoteId"] = "existing-quote" } });
        await AssertSuppressed(store, invoice);
    }

    private static async Task AssertSuppressed(StoreData store, InvoiceEntity invoice)
    {
        // Absent services prove exclusion happens before any DB read, quote creation or mint request.
        var handler = new CashuMeltPaymentMethodHandler(null!, null!, NullLogger<CashuMeltPaymentMethodHandler>.Instance);
        var context = new PaymentMethodContext(store, new StoreBlob(), new JObject(), handler, invoice, new InvoiceLogs(), null!);
        await context.BeforeFetchingRates();
        await context.CreatePaymentPrompt(checkPaymentMethodConflicts: false);
        Assert.Equal(PaymentMethodContext.ContextStatus.Failed, context.Status);
        Assert.Null(context.Prompt.Destination); Assert.Null(context.Prompt.Details); Assert.Empty(context.TrackedDestinations);
    }
}
