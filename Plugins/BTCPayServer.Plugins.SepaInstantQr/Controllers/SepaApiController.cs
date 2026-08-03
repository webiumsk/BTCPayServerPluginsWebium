#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Models;
using BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SepaInstantQr.Controllers;

/// <summary>
/// Greenfield API for managing SEPA Instant QR from external control
/// panels (satflux). Authenticated with a store-scoped API key holding the
/// CanModifyStoreSettings permission - the same trust level as the
/// cookie-auth settings UI. Certificate material travels only inbound;
/// responses expose the *Set flag and the parsed identity, never secrets.
/// </summary>
[ApiController]
[Route("api/v1/stores/{storeId}/plugins/sepa-instant-qr")]
[Authorize(Policy = Policies.CanModifyStoreSettings,
           AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class SepaApiController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly SepaConfigService _configService;
    private readonly SepaCertificateService _certificateService;
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaMatchingService _matchingService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly Dictionary<string, IPaymentConfirmationSource> _confirmationSources;

    public SepaApiController(
        StoreRepository storeRepository,
        SepaConfigService configService,
        SepaCertificateService certificateService,
        SepaDbContextFactory dbContextFactory,
        SepaMatchingService matchingService,
        PaymentMethodHandlerDictionary handlers,
        IEnumerable<IPaymentConfirmationSource> confirmationSources)
    {
        _storeRepository = storeRepository;
        _configService = configService;
        _certificateService = certificateService;
        _dbContextFactory = dbContextFactory;
        _matchingService = matchingService;
        _handlers = handlers;
        _confirmationSources = confirmationSources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        return Ok(Map(settings));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        string storeId, [FromBody] SepaUpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (!IbanValidator.IsValid(request.Iban))
            return Problem(statusCode: 400, detail: "The IBAN is not valid (checksum failed).");

        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound();

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken)
                       ?? new SepaStoreSettings { StoreId = storeId };
        settings.Enabled = request.Enabled;
        settings.CountryProfile = request.CountryProfile.ToUpperInvariant();
        settings.Iban = IbanValidator.Normalize(request.Iban);
        settings.Beneficiary = request.Beneficiary.Trim();
        settings.Bic = string.IsNullOrWhiteSpace(request.Bic) ? null : request.Bic.Trim().ToUpperInvariant();
        settings.Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        settings.ConfirmationBackend = request.ConfirmationBackend;
        settings.SkQrVariant = request.SkQrVariant;
        settings.CheckoutConfirmEnabled = request.CheckoutConfirmEnabled;
        settings.AmountTolerance = request.AmountTolerance;

        if (request.ConfirmationBackend.StartsWith("nop-", StringComparison.Ordinal)
            && string.IsNullOrEmpty(settings.NopVatsk))
            return Problem(statusCode: 400,
                detail: "NOP backends need the eKasa cash-register certificate - upload it first (POST certificate).");

        if (request.ConfirmationBackend == Services.Confirmation.Fio.FioSource.BackendId
            && !_configService.GetCredentials(settings).HasFioToken)
            return Problem(statusCode: 400,
                detail: "The Fio backend needs an API token - store it first (POST fio-token).");

        // Environment changes without re-uploading material go through the
        // certificate service so the encrypted blob stays consistent; an
        // omitted nopEnvironment keeps the stored one instead of silently
        // resetting a PROD store to INT.
        var environment = request.NopEnvironment
                          ?? _configService.GetCredentials(settings).NopEnvironment;
        var error = _certificateService.Apply(settings,
            new SepaCertificateUpload(null, null, null, null, environment));
        if (error is not null)
            return Problem(statusCode: 400, detail: error);

        await _configService.SaveSettingsAsync(settings, cancellationToken);
        await SyncPaymentMethodConfigAsync(store, request.Enabled);

        return Ok(Map(await _configService.GetSettingsAsync(storeId, cancellationToken)));
    }

    [HttpPost("certificate")]
    public async Task<IActionResult> UploadCertificate(
        string storeId, [FromBody] SepaUploadCertificateRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (string.IsNullOrWhiteSpace(request.PfxBase64)
            && string.IsNullOrWhiteSpace(request.CertPem)
            && string.IsNullOrWhiteSpace(request.KeyPem))
            return Problem(statusCode: 400,
                detail: "Provide either pfxBase64 (with pfxPassword if set) or the certPem/keyPem pair.");

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return Problem(statusCode: 400, detail: "Save the settings first (PUT settings).");

        var error = _certificateService.Apply(settings, new SepaCertificateUpload(
            request.PfxBase64, request.PfxPassword, request.CertPem, request.KeyPem,
            request.NopEnvironment ?? _configService.GetCredentials(settings).NopEnvironment));
        if (error is not null)
            return Problem(statusCode: 400, detail: error);

        await _configService.SaveSettingsAsync(settings, cancellationToken);
        return Ok(Map(settings));
    }

    [HttpDelete("certificate")]
    public async Task<IActionResult> ClearCertificate(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return NotFound();

        var environment = _configService.GetCredentials(settings).NopEnvironment;
        _certificateService.Clear(settings, environment);

        // A NOP backend without a certificate cannot confirm anything -
        // fall back to manual so the store keeps working.
        if (settings.ConfirmationBackend.StartsWith("nop-", StringComparison.Ordinal))
            settings.ConfirmationBackend = "manual";

        await _configService.SaveSettingsAsync(settings, cancellationToken);
        return Ok(Map(settings));
    }

    [HttpPost("fio-token")]
    public async Task<IActionResult> SetFioToken(
        string storeId, [FromBody] SepaFioTokenRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return Problem(statusCode: 400, detail: "Save the settings first (PUT settings).");

        var error = await _configService.TrySetFioTokenAsync(settings, request.Token, cancellationToken);
        if (error is not null)
            return Problem(statusCode: 400, detail: error);

        await _configService.SaveSettingsAsync(settings, cancellationToken);
        return Ok(Map(settings));
    }

    [HttpDelete("fio-token")]
    public async Task<IActionResult> ClearFioToken(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return NotFound();

        _configService.ClearFioToken(settings);

        if (settings.ConfirmationBackend == Services.Confirmation.Fio.FioSource.BackendId)
            settings.ConfirmationBackend = "manual";

        await _configService.SaveSettingsAsync(settings, cancellationToken);
        return Ok(Map(settings));
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestBackend(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return Problem(statusCode: 400, detail: "Save the settings first (PUT settings).");

        if (!_confirmationSources.TryGetValue(settings.ConfirmationBackend, out var source))
            return Problem(statusCode: 400,
                detail: $"Unknown confirmation backend '{settings.ConfirmationBackend}'.");

        var result = await source.TestAsync(settings, cancellationToken);
        return Ok(new SepaTestResultData { Ok = result.Ok, Message = result.Message });
    }

    [HttpGet("payment-requests")]
    public async Task<IActionResult> ListPaymentRequests(
        string storeId, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        string[]? states = state?.ToLowerInvariant() switch
        {
            null or "" => [SepaPaymentRequestState.Pending, SepaPaymentRequestState.ManualReview],
            "pending" => [SepaPaymentRequestState.Pending],
            "review" => [SepaPaymentRequestState.ManualReview],
            _ => null,
        };
        if (states is null)
            return Problem(statusCode: 400, detail: "state must be 'pending' or 'review'.");

        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var rows = await ctx.SepaPaymentRequests
                .AsNoTracking()
                .Where(r => r.StoreId == storeId && states.Contains(r.State))
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync(cancellationToken);

            return Ok(rows.Select(r => new SepaPaymentRequestData
            {
                Reference = r.Reference,
                InvoiceId = r.InvoiceId,
                State = r.State,
                AmountDue = r.AmountDue,
                Currency = r.Currency,
                CreatedAt = r.CreatedAt,
                ReviewReason = r.ReviewReason,
            }));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return Ok(Array.Empty<SepaPaymentRequestData>());
        }
    }

    /// <summary>
    /// Amount-verified confirmation report from an external channel
    /// (satflux b-mail): runs through the shared matching service, so a
    /// mismatched amount or currency routes to manual review and never
    /// settles - unlike the trusted manual confirm below.
    /// </summary>
    [HttpPost("payment-requests/report")]
    public async Task<IActionResult> ReportPayment(
        string storeId, [FromBody] SepaReportPaymentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        if (settings is null)
            return Problem(statusCode: 400, detail: "Save the settings first (PUT settings).");

        var outcome = await _matchingService.ProcessAsync(
            "bmail",
            new ConfirmedPayment(
                request.Reference,
                request.Amount,
                request.Currency.ToUpperInvariant(),
                RawJson: null,
                DedupKey: string.IsNullOrWhiteSpace(request.DedupKey) ? null : $"bmail:{request.DedupKey}"),
            settings.AmountTolerance,
            cancellationToken);

        return Ok(new { outcome = outcome switch
        {
            MatchOutcome.Settled => "settled",
            MatchOutcome.Duplicate => "duplicate",
            MatchOutcome.ManualReview => "review",
            _ => "unknown",
        } });
    }

    /// <summary>
    /// Manual confirmation - same path as the settings UI: the shared
    /// matching service settles through BTCPay's normal invoice lifecycle
    /// (webhooks fire, POS flips to paid).
    /// </summary>
    [HttpPost("payment-requests/{reference}/confirm")]
    public async Task<IActionResult> ConfirmManually(
        string storeId, string reference, CancellationToken cancellationToken)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var request = await ctx.SepaPaymentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Reference == reference && r.StoreId == storeId, cancellationToken);
        if (request is null)
            return NotFound();

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        var outcome = await _matchingService.ProcessAsync(
            $"manual:{User.Identity?.Name ?? "greenfield"}",
            new ConfirmedPayment(reference, request.AmountDue, request.Currency, RawJson: null, DedupKey: null),
            settings?.AmountTolerance ?? 0m,
            cancellationToken);

        return outcome switch
        {
            MatchOutcome.Settled => Ok(new { outcome = "settled" }),
            MatchOutcome.Duplicate => Ok(new { outcome = "duplicate" }),
            _ => Problem(statusCode: 409, detail: $"Payment {reference} could not be settled ({outcome})."),
        };
    }

    private SepaSettingsData Map(SepaStoreSettings? settings)
    {
        if (settings is null)
            return new SepaSettingsData { Configured = false };

        var credentials = _configService.GetCredentials(settings);
        return new SepaSettingsData
        {
            Configured = true,
            Enabled = settings.Enabled,
            CountryProfile = settings.CountryProfile,
            Iban = settings.Iban,
            Beneficiary = settings.Beneficiary,
            Bic = settings.Bic,
            Message = settings.Message,
            ConfirmationBackend = settings.ConfirmationBackend,
            SkQrVariant = settings.SkQrVariant,
            AmountTolerance = settings.AmountTolerance,
            NopEnvironment = credentials.NopEnvironment,
            NopCertSet = credentials.HasNopCertificate,
            FioTokenSet = credentials.HasFioToken,
            CheckoutConfirmEnabled = settings.CheckoutConfirmEnabled,
            NopVatsk = settings.NopVatsk,
            NopPokladnica = settings.NopPokladnica,
        };
    }

    private async Task SyncPaymentMethodConfigAsync(StoreData store, bool enabled)
    {
        if (!_handlers.Support(SepaInstantQrPlugin.SepaPaymentMethodId))
            return;

        store.SetPaymentMethodConfig(
            _handlers[SepaInstantQrPlugin.SepaPaymentMethodId],
            new SepaPaymentMethodConfig { Enabled = enabled });
        await _storeRepository.UpdateStore(store);
    }
}
