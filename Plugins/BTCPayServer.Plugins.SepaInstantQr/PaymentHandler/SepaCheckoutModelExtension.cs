#nullable enable
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;

public class SepaCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "SepaInstantQrCheckout";

    public PaymentMethodId PaymentMethodId => SepaInstantQrPlugin.SepaPaymentMethodId;
    public string Image => "";
    public string Badge => "€";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not SepaPaymentMethodHandler handler)
            return;

        if (context.Model.Status != "New")
            return;

        if (context.Prompt.Details is null)
            return;

        SepaPromptDetails? details;
        try
        {
            details = context.Prompt.Details.ToObject<SepaPromptDetails>(handler.Serializer);
        }
        catch
        {
            return;
        }

        if (details is null || string.IsNullOrWhiteSpace(details.QrPayload))
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        context.Model.AdditionalData["sepaQrPayload"] = JToken.FromObject(details.QrPayload);
        context.Model.AdditionalData["sepaReference"] = JToken.FromObject(details.Reference);
        context.Model.AdditionalData["sepaIban"] = JToken.FromObject(details.Iban);
        context.Model.AdditionalData["sepaBeneficiary"] = JToken.FromObject(details.Beneficiary);
        context.Model.AdditionalData["sepaAmount"] = JToken.FromObject(details.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        context.Model.AdditionalData["sepaProfile"] = JToken.FromObject(details.CountryProfile);
        context.Model.AdditionalData["sepaCurrency"] = JToken.FromObject(string.IsNullOrEmpty(details.Currency) ? "EUR" : details.Currency);
        context.Model.AdditionalData["sepaCheckoutConfirmEnabled"] = JToken.FromObject(details.CheckoutConfirmEnabled);

        context.Model.Address = details.Iban;
        context.Model.ShowRecommendedFee = false;
    }
}
