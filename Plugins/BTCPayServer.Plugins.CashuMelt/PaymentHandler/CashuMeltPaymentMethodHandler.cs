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
        context.State = null;
        var existingPrompt = context.InvoiceEntity.GetPaymentPrompt(PaymentMethodId);
        var hasDetails = existingPrompt?.Details is { Type: not (JTokenType.Null or JTokenType.Undefined) } detailsHint
            && (detailsHint.Type != JTokenType.Object || detailsHint.HasValues);
        if (existingPrompt is not null && (!string.IsNullOrWhiteSpace(existingPrompt.Destination) || hasDetails))
        {
            // A materialized prompt is never replaced by a new quote if its binding cannot be verified.
            if (context.InvoiceEntity.StoreId != context.Store.Id || string.IsNullOrWhiteSpace(existingPrompt.Destination)
                || !CashuMeltCheckoutModelExtension.TryGetPromptDetails(this, existingPrompt.Details, out var promptDetails)
                || promptDetails.QuoteId != existingPrompt.Destination) return;
            var bound = await _quotes.FindExistingPromptAsync(context.InvoiceEntity.Id, context.Store.Id, existingPrompt.Destination);
            if (bound is not null)
            {
                context.State = bound;
                context.Prompt.Currency = bound.Unit == "usd" ? "USD" : "BTC";
                context.Prompt.Divisibility = bound.Unit == "usd" ? 2 : 8;
                context.Prompt.PaymentMethodFee = 0m;
            }
            return;
        }

        // The host aggregates this phase without per-method exception handling. Leave only
        // Cashu unavailable; ConfigurePrompt fails inside the host's per-method boundary.
        if (HasConfiguredLightningWallet(context.Store)) return;
        var settings = await _configService.GetEnabledSettingsAsync(context.Store.Id);
        if (settings is null) return;
        context.Prompt.Currency = settings.Unit == "usd" ? "USD" : "BTC";
        context.Prompt.Divisibility = settings.Unit == "usd" ? 2 : 8;
        context.Prompt.PaymentMethodFee = 0m;
        context.State = settings;
    }

    /// <summary>Configuration presence is authoritative, including excluded or temporarily failing wallets.</summary>
    public static bool HasConfiguredLightningWallet(StoreData store) =>
        store.GetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC")) is not null
        || store.GetPaymentMethodConfig(PaymentTypes.LNURL.GetPaymentMethodId("BTC")) is not null;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is CashuMeltPromptDetails existing)
        {
            ApplyPrompt(context, existing);
            return;
        }
        if (HasConfiguredLightningWallet(context.Store))
            throw new PaymentMethodUnavailableException("CashuMelt checkout is unavailable while a Lightning wallet is configured");
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

        ApplyPrompt(context, new CashuMeltPromptDetails { QuoteId = quoteId, Bolt11Invoice = bolt11,
            AmountSats = amountSats, Unit = unit });
        _logger.LogInformation("Created CashuMelt prompt for invoice {InvoiceId}: quote {QuoteId}", invoice.Id, quoteId);
    }

    private void ApplyPrompt(PaymentMethodContext context, CashuMeltPromptDetails details)
    {
        context.Prompt.Destination = details.QuoteId;
        context.Prompt.Details = JObject.FromObject(details, Serializer);
        context.TrackedDestinations.Add(details.QuoteId);
        context.AdditionalSearchTerms.Add(details.QuoteId);
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
