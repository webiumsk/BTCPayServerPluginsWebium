#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using BTCPayServer.Plugins.SepaInstantQr.Services.Qr;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;

public class SepaPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly SepaConfigService _configService;
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly IReadOnlyDictionary<string, IQrPayloadBuilder> _qrBuilders;
    private readonly IReadOnlyDictionary<string, IPaymentConfirmationSource> _confirmationSources;
    private readonly ILogger<SepaPaymentMethodHandler> _logger;

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; } = SepaInstantQrPlugin.SepaPaymentMethodId;

    public SepaPaymentMethodHandler(
        SepaConfigService configService,
        SepaDbContextFactory dbContextFactory,
        IEnumerable<IQrPayloadBuilder> qrBuilders,
        IEnumerable<IPaymentConfirmationSource> confirmationSources,
        ILogger<SepaPaymentMethodHandler> logger)
    {
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _qrBuilders = qrBuilders.ToDictionary(b => b.Profile, StringComparer.OrdinalIgnoreCase);
        _confirmationSources = confirmationSources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        (_, Serializer) = BlobSerializer.CreateSerializer(null as NBitcoin.Network);
    }

    public async Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var settings = await _configService.GetEnabledSettingsAsync(context.Store.Id);
        if (settings is null || !IbanValidator.IsValid(settings.Iban))
        {
            context.State = null;
            return;
        }

        // Currency gate per profile: the CZ profile accepts CZK (QR Platba
        // is CZK-native; PT:IP = domestic CERTIS instant) alongside EUR;
        // SK/EU stay EUR-only (SEPA instant). Unsupported invoice currency
        // -> the tab simply does not exist on that invoice.
        var currency = context.InvoiceEntity.Currency?.ToUpperInvariant() ?? "";
        if (!SupportsCurrency(settings.CountryProfile, currency))
        {
            context.State = null;
            return;
        }

        context.Prompt.Currency = currency;
        context.Prompt.Divisibility = 2;
        context.Prompt.PaymentMethodFee = 0m;
        context.State = settings;
    }

    /// <summary>
    /// Which invoice currencies a profile can charge: CZ additionally
    /// handles CZK - both the SPD payload (CC:CZK) and matching carry the
    /// request currency end to end, settling 1:1 against the invoice.
    /// </summary>
    internal static bool SupportsCurrency(string countryProfile, string currency)
        => currency switch
        {
            "EUR" => true,
            "CZK" => countryProfile.Equals("CZ", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is not SepaStoreSettings settings)
            throw new PaymentMethodUnavailableException("SEPA Instant QR is not configured for this store");

        var builderKey = ResolveQrBuilderKey(settings);
        if (!_qrBuilders.TryGetValue(builderKey, out var qrBuilder))
            throw new PaymentMethodUnavailableException($"Unknown SEPA QR profile '{builderKey}'");

        var invoice = context.InvoiceEntity;
        var due = context.Prompt.Calculate().Due;

        var reference = await AcquireReferenceAsync(settings);
        var qrPayload = qrBuilder.Build(new SepaQrRequest(
            settings.Iban,
            settings.Beneficiary,
            due,
            reference,
            settings.Message,
            settings.Bic,
            Currency: context.Prompt.Currency));

        context.Prompt.Destination = IbanValidator.Normalize(settings.Iban);
        context.Prompt.Details = JObject.FromObject(new SepaPromptDetails
        {
            Reference = reference,
            QrPayload = qrPayload,
            Iban = IbanValidator.Normalize(settings.Iban),
            Beneficiary = settings.Beneficiary,
            Amount = due,
            CountryProfile = settings.CountryProfile,
        }, Serializer);

        context.TrackedDestinations.Add(reference);
        context.AdditionalSearchTerms.Add(reference);

        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.SepaPaymentRequests.AddAsync(new SepaPaymentRequest
        {
            Reference = reference,
            ReferenceKind = settings.CountryProfile.Equals("CZ", StringComparison.OrdinalIgnoreCase) ? "VS" : "E2E",
            InvoiceId = invoice.Id,
            StoreId = invoice.StoreId,
            Backend = settings.ConfirmationBackend,
            State = SepaPaymentRequestState.Pending,
            AmountDue = due,
            Currency = context.Prompt.Currency,
            Iban = IbanValidator.Normalize(settings.Iban),
            QrPayload = qrPayload,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        _logger.LogInformation(
            "Created SEPA prompt for invoice {InvoiceId}: reference {Reference} ({Profile})",
            invoice.Id, reference, settings.CountryProfile);
    }

    private async Task<string> AcquireReferenceAsync(SepaStoreSettings settings)
    {
        // A backend can supply the reference (NOP generateNewTransactionId
        // from the NOP phase on); every v0.1 backend returns null → local.
        if (_confirmationSources.TryGetValue(settings.ConfirmationBackend, out var source))
        {
            var acquired = await source.AcquireReferenceAsync(settings, default);
            if (!string.IsNullOrWhiteSpace(acquired))
                return acquired!;
        }

        return settings.CountryProfile.Equals("CZ", StringComparison.OrdinalIgnoreCase)
            ? PaymentReferenceGenerator.NewVariableSymbol()
            : PaymentReferenceGenerator.NewEndToEndId();
    }

    /// <summary>
    /// The SK profile has two QR variants (PayMe link vs PAY by square);
    /// other profiles map 1:1 to their builder.
    /// </summary>
    internal static string ResolveQrBuilderKey(SepaStoreSettings settings)
        => settings.CountryProfile.Equals("SK", StringComparison.OrdinalIgnoreCase)
           && string.Equals(settings.SkQrVariant, "bysquare", StringComparison.OrdinalIgnoreCase)
            ? Services.Qr.PayBySquarePayloadBuilder.ProfileKey
            : settings.CountryProfile;

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<SepaPromptDetails>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(SepaPromptDetails)}");
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config?.ToObject<SepaPaymentMethodConfig>(Serializer) ?? new SepaPaymentMethodConfig();
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<SepaPaymentData>(Serializer)
            ?? throw new FormatException($"Invalid {nameof(SepaPaymentData)}");
    }
}
