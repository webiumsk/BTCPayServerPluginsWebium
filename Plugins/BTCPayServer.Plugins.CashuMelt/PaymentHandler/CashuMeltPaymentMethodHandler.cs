#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

public class CashuMeltPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltQuoteService _quotes;
    private readonly ILogger<CashuMeltPaymentMethodHandler> _logger;

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; } = CashuMeltPlugin.CashuMeltPaymentMethodId;

    public CashuMeltPaymentMethodHandler(
        CashuMeltConfigService configService,
        CashuMeltQuoteService quotes,
        ILogger<CashuMeltPaymentMethodHandler> logger)
    {
        _configService = configService;
        _quotes = quotes;
        _logger = logger;
        (_, Serializer) = BlobSerializer.CreateSerializer(null as NBitcoin.Network);
    }

    public async Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var settings = await _configService.GetEnabledSettingsAsync(context.Store.Id);
        if (settings == null)
        {
            context.State = null;
            return;
        }

        context.Prompt.Currency = settings.Unit == "usd" ? "USD" : "BTC";
        context.Prompt.Divisibility = settings.Unit == "usd" ? 2 : 8;
        context.Prompt.PaymentMethodFee = 0m;
        context.State = settings;

        // Parallel-fallback mode: when the store also offers BTC Lightning, defer this
        // prompt (per-method lazy activation). The mint quote and its settlement record
        // are then created only if the Cashu method actually activates in checkout -
        // the Lightning prompt failed to create, or the customer picked the Cashu tab.
        // Without this, every Lightning-paid invoice leaves an orphaned PENDING quote.
        if (ShouldDeferPrompt(context.InvoiceEntity, context.Store))
        {
            context.Prompt.Inactive = true;
        }
    }

    /// <summary>
    /// Whether the prompt being built should wait for checkout activation. Never true
    /// during activation itself - the invoice already carries our prompt, and the fresh
    /// activation context must be allowed to configure it.
    /// </summary>
    public static bool ShouldDeferPrompt(InvoiceEntity invoice, StoreData store)
        => invoice.GetPaymentPrompt(CashuMeltPlugin.CashuMeltPaymentMethodId) is null
           && ShouldDeferBehindLightning(store);

    /// <summary>A BTC Lightning method (LN or LNURL) is configured on the store and not excluded from checkout.</summary>
    public static bool ShouldDeferBehindLightning(StoreData store)
    {
        var configs = store.GetPaymentMethodConfigs();
        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();

        // Checkout treats both BTC-LN and BTC-LNURL as Lightning - a store can exclude
        // one while still offering the other, so either counts as "Lightning available".
        foreach (var pmId in new[] { PaymentTypes.LN.GetPaymentMethodId("BTC"), PaymentTypes.LNURL.GetPaymentMethodId("BTC") })
        {
            if (configs.ContainsKey(pmId) && !excluded.Match(pmId))
                return true;
        }

        return false;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is not CashuMelt.Data.Entities.CashuMeltStoreSettings settings)
        {
            throw new PaymentMethodUnavailableException("CashuMelt is not configured for this store");
        }

        try
        {
            CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(settings);
        }
        catch (InvalidOperationException ex)
        {
            throw new PaymentMethodUnavailableException(ex.Message);
        }

        var invoice = context.InvoiceEntity;
        var due = context.Prompt.Calculate().Due;
        string unit = settings.Unit ?? "sat";
        long amountSats = CashuMeltAmountCalculator.ComputeMintAmount(
            invoice.Currency, invoice.Price, due, unit);

        // The same durable creation path is used by checkout and settlement services.
        // Never publish the mint's bearer quote ID or create a row without its payout snapshot.
        var (quoteId, bolt11, error) = await _quotes.CreateQuoteAsync(invoice.Id, invoice.StoreId, amountSats, unit);
        if (error is not null || quoteId is null || bolt11 is null)
            throw new PaymentMethodUnavailableException(error ?? "Failed to create CashuMelt mint quote");

        context.Prompt.Destination = quoteId;
        context.Prompt.Details = JObject.FromObject(new CashuMeltPromptDetails
        {
            QuoteId = quoteId,
            Bolt11Invoice = bolt11,
            AmountSats = amountSats,
            Unit = unit
        }, Serializer);
        context.TrackedDestinations.Add(quoteId);
        context.AdditionalSearchTerms.Add(quoteId);

        _logger.LogInformation("Created CashuMelt prompt for invoice {InvoiceId}: quote {QuoteId}", invoice.Id, quoteId);
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<CashuMeltPromptDetails>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(CashuMeltPromptDetails)}");
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config?.ToObject<CashuMeltPaymentMethodConfig>(Serializer) ?? new CashuMeltPaymentMethodConfig();
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<CashuMeltPaymentData>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(CashuMeltPaymentData)}");
    }
}
