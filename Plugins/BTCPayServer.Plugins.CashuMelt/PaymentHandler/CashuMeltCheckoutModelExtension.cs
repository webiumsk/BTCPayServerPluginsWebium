using System;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

public class CashuMeltCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "CashuMeltCheckout";

    public PaymentMethodId PaymentMethodId => CashuMeltPlugin.CashuMeltPaymentMethodId;
    public string Image => "";
    public string Badge => "₿";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not CashuMeltPaymentMethodHandler handler)
            return;

        // Only show the CashuMelt checkout body while the invoice is awaiting payment ("New").
        // For every other state (Processing, Settled, Expired, Invalid) let BTCPay's
        // standard checkout render — it shows celebration, the return-to-merchant link,
        // and auto-redirect.  Replacing the body for non-New invoices causes our component
        // to restart polling and trigger an infinite reload loop.
        if (context.Model.Status != "New")
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;

        var promptDetails = (CashuMeltPromptDetails)handler.ParsePaymentPromptDetails(context.Prompt.Details);

        context.Model.AdditionalData["cashuQuoteId"] = JToken.FromObject(promptDetails.QuoteId);
        context.Model.AdditionalData["cashuBolt11"] = JToken.FromObject(promptDetails.Bolt11Invoice);
        context.Model.AdditionalData["cashuAmountSats"] = JToken.FromObject(promptDetails.AmountSats);
        context.Model.AdditionalData["cashuUnit"] = JToken.FromObject(promptDetails.Unit);

        var uri = promptDetails.Bolt11Invoice.StartsWith("lnbc", StringComparison.OrdinalIgnoreCase)
            ? $"lightning:{promptDetails.Bolt11Invoice}"
            : promptDetails.Bolt11Invoice;
        context.Model.AdditionalData["cashuUri"] = JToken.FromObject(uri);

        context.Model.Address = $"{promptDetails.AmountSats} {promptDetails.Unit}";
        context.Model.ShowRecommendedFee = false;
    }
}
