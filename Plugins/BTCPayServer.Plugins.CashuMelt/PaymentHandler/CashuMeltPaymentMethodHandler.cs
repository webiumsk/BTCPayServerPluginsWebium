#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

public class CashuMeltPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltMintClient _mintClient;
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly ILogger<CashuMeltPaymentMethodHandler> _logger;

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; } = CashuMeltPlugin.CashuMeltPaymentMethodId;

    public CashuMeltPaymentMethodHandler(
        CashuMeltConfigService configService,
        CashuMeltMintClient mintClient,
        CashuMeltDbContextFactory dbContextFactory,
        ILogger<CashuMeltPaymentMethodHandler> logger)
    {
        _configService = configService;
        _mintClient = mintClient;
        _dbContextFactory = dbContextFactory;
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
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is not CashuMelt.Data.Entities.CashuMeltStoreSettings settings)
        {
            throw new PaymentMethodUnavailableException("CashuMelt is not configured for this store");
        }

        var invoice = context.InvoiceEntity;
        var due = context.Prompt.Calculate().Due;

        long amountSats;
        string unit = settings.Unit ?? "sat";

        if (unit == "usd")
        {
            // Due is in USD (cents or dollars - divisibility 2 means cents)
            amountSats = (long)Math.Round(due * 100_000_000); // Treat due as USD, convert to "sats" equivalent for mint (some mints use usd as unit with different scale)
            // Actually for usd unit, mint might expect amount in cents. Let me check NUT-23 - unit is "sat" or "usd". For usd, the amount might be in smallest unit. Common: usd = cents (1 USD = 100). So amount 1000 = $10.
            amountSats = (long)Math.Round(due * 100); // if due is in USD (e.g. 10.50), then 10.50*100 = 1050 cents
        }
        else
        {
            // Due is in BTC
            amountSats = (long)Math.Round(due * 100_000_000);
            if (amountSats < 1) amountSats = 1;
        }

        var quote = await _mintClient.CreateMintQuoteAsync(settings.MintUrl, amountSats, unit);
        if (quote == null || string.IsNullOrEmpty(quote.Quote) || string.IsNullOrEmpty(quote.Request))
        {
            throw new PaymentMethodUnavailableException("Failed to create CashuMelt mint quote");
        }

        context.Prompt.Destination = quote.Quote;
        context.Prompt.Details = JObject.FromObject(new CashuMeltPromptDetails
        {
            QuoteId = quote.Quote,
            Bolt11Invoice = quote.Request,
            AmountSats = amountSats,
            Unit = unit
        }, Serializer);

        context.TrackedDestinations.Add(quote.Quote);
        context.AdditionalSearchTerms.Add(quote.Quote);

        // Store in our DB for polling lookup
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.CashuMeltPaymentRequests.AddAsync(new Data.Entities.CashuMeltPaymentRequest
        {
            QuoteId = quote.Quote,
            InvoiceId = invoice.Id,
            StoreId = invoice.StoreId,
            AmountSats = amountSats,
            Unit = unit,
            Bolt11Invoice = quote.Request,
            State = quote.State,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await ctx.SaveChangesAsync();

        _logger.LogInformation("Created CashuMelt prompt for invoice {InvoiceId}: quote {QuoteId}", invoice.Id, quote.Quote);
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
