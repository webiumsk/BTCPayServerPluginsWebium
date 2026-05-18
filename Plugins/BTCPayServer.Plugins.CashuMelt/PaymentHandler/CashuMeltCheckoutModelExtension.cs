#nullable enable
using System;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

public class CashuMeltCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "CashuMeltCheckout";

    private readonly ILogger<CashuMeltCheckoutModelExtension> _logger;

    public CashuMeltCheckoutModelExtension()
        : this(NullLogger<CashuMeltCheckoutModelExtension>.Instance)
    {
    }

    public CashuMeltCheckoutModelExtension(ILogger<CashuMeltCheckoutModelExtension> logger)
    {
        _logger = logger;
    }

    public PaymentMethodId PaymentMethodId => CashuMeltPlugin.CashuMeltPaymentMethodId;
    public string Image => "";
    public string Badge => "₿";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not CashuMeltPaymentMethodHandler handler)
            return;

        if (context.Model.Status != "New")
            return;

        if (!TryGetPromptDetails(handler, context.Prompt.Details, out var promptDetails))
        {
            _logger.LogWarning(
                "CashuMelt checkout skipped for invoice {InvoiceId}: payment prompt details missing or invalid",
                context.InvoiceEntity.Id);
            return;
        }

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;

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

    internal static bool TryGetPromptDetails(
        CashuMeltPaymentMethodHandler handler,
        JToken? details,
        out CashuMeltPromptDetails promptDetails)
    {
        promptDetails = null!;
        if (details is null || details.Type is JTokenType.Null or JTokenType.Undefined)
            return false;

        try
        {
            var parsed = details.ToObject<CashuMeltPromptDetails>(handler.Serializer);
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.QuoteId) ||
                string.IsNullOrWhiteSpace(parsed.Bolt11Invoice))
                return false;

            promptDetails = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
