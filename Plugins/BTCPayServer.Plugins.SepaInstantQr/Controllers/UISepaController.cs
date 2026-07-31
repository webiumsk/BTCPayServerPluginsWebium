#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
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

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[AutoValidateAntiforgeryToken]
[Route("plugins/{storeId}/sepainstantqr")]
public class UISepaController : Controller
{
    // The view renders fields with the "Settings." page-model prefix, so
    // manual ModelState errors must use the prefixed key to attach to the
    // field's asp-validation-for span (the summary shows any key either way).
    private const string NopCertFieldKey = $"Settings.{nameof(Models.SepaSettingsViewModel.NopPfxFile)}";

    private readonly StoreRepository _storeRepository;
    private readonly SepaConfigService _configService;
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaMatchingService _matchingService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly System.Collections.Generic.Dictionary<string, IPaymentConfirmationSource> _confirmationSources;

    public UISepaController(
        StoreRepository storeRepository,
        SepaConfigService configService,
        SepaDbContextFactory dbContextFactory,
        SepaMatchingService matchingService,
        PaymentMethodHandlerDictionary handlers,
        System.Collections.Generic.IEnumerable<IPaymentConfirmationSource> confirmationSources)
    {
        _storeRepository = storeRepository;
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _matchingService = matchingService;
        _handlers = handlers;
        _confirmationSources = confirmationSources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound();

        var model = await BuildPageModelAsync(storeId);
        return View(model);
    }

    [HttpPost("")]
    public async Task<IActionResult> Settings(
        string storeId,
        // The view binds fields as "Settings.X" (page model) - without the
        // prefix nothing binds and Required validation silently rejects the
        // save (same pattern as UICashuMeltController).
        [Microsoft.AspNetCore.Mvc.Bind(Prefix = "Settings")] SepaSettingsViewModel model)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound();

        if (!ModelState.IsValid)
        {
            var page = await BuildPageModelAsync(storeId);
            page.Settings = model;
            return View(page);
        }

        var settings = await _configService.GetSettingsAsync(storeId) ?? new SepaStoreSettings { StoreId = storeId };
        settings.Enabled = model.Enabled;
        settings.CountryProfile = model.CountryProfile.ToUpperInvariant();
        settings.Iban = IbanValidator.Normalize(model.Iban);
        settings.Beneficiary = model.Beneficiary.Trim();
        settings.Bic = string.IsNullOrWhiteSpace(model.Bic) ? null : model.Bic.Trim().ToUpperInvariant();
        settings.Message = string.IsNullOrWhiteSpace(model.Message) ? null : model.Message.Trim();
        settings.ConfirmationBackend = model.ConfirmationBackend;
        settings.AmountTolerance = model.AmountTolerance;

        if (!await ApplyNopCertificateAsync(settings, model))
        {
            var page = await BuildPageModelAsync(storeId);
            page.Settings = model;
            return View(page);
        }

        if (model.ConfirmationBackend.StartsWith("nop-", StringComparison.Ordinal)
            && string.IsNullOrEmpty(settings.NopVatsk))
        {
            ModelState.AddModelError(NopCertFieldKey,
                "NOP backends need the eKasa cash-register certificate - upload it below.");
            var page = await BuildPageModelAsync(storeId);
            page.Settings = model;
            return View(page);
        }

        await _configService.SaveSettingsAsync(settings);

        if (_handlers.Support(SepaInstantQrPlugin.SepaPaymentMethodId))
        {
            store.SetPaymentMethodConfig(
                _handlers[SepaInstantQrPlugin.SepaPaymentMethodId],
                new SepaPaymentMethodConfig { Enabled = model.Enabled });
            await _storeRepository.UpdateStore(store);
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "SEPA Instant QR settings saved.",
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    /// <summary>
    /// Applies the uploaded/cleared NOP certificate to the settings entity.
    /// Returns false (with ModelState errors) when the upload is invalid.
    /// Certificate material goes straight into the encrypted credentials
    /// blob - it is never logged or echoed back.
    /// </summary>
    private async Task<bool> ApplyNopCertificateAsync(SepaStoreSettings settings, SepaSettingsViewModel model)
    {
        var credentials = _configService.GetCredentials(settings);

        if (model.ClearNopCertificate)
        {
            _configService.ApplyCredentials(settings, credentials with
            {
                NopCertificatePem = null,
                NopPrivateKeyPem = null,
                NopPfxBase64 = null,
                NopPfxPassword = null,
                NopEnvironment = model.NopEnvironment,
            });
            settings.NopVatsk = null;
            settings.NopPokladnica = null;
            return true;
        }

        SepaBackendCredentials updated = credentials with { NopEnvironment = model.NopEnvironment };
        var uploaded = false;

        if (model.NopPfxFile is not null)
        {
            using var stream = new System.IO.MemoryStream();
            await model.NopPfxFile.CopyToAsync(stream);
            updated = updated with
            {
                NopPfxBase64 = Convert.ToBase64String(stream.ToArray()),
                NopPfxPassword = model.NopPfxPassword,
                NopCertificatePem = null,
                NopPrivateKeyPem = null,
            };
            uploaded = true;
        }
        else if (model.NopCertPemFile is not null && model.NopKeyPemFile is not null)
        {
            updated = updated with
            {
                NopCertificatePem = await ReadFormFileAsync(model.NopCertPemFile),
                NopPrivateKeyPem = await ReadFormFileAsync(model.NopKeyPemFile),
                NopPfxBase64 = null,
                NopPfxPassword = null,
            };
            uploaded = true;
        }

        if (uploaded)
        {
            try
            {
                using var certificate = Services.Confirmation.Nop.NopCertificateLoader.Load(updated);
                if (!certificate.HasPrivateKey)
                {
                    ModelState.AddModelError(NopCertFieldKey,
                        "The certificate has no private key - mTLS authentication needs it (upload the key file or a complete .p12).");
                    return false;
                }

                // NotBefore/NotAfter are local-time DateTimes - convert
                // before comparing against UtcNow.
                if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                {
                    ModelState.AddModelError(NopCertFieldKey,
                        $"The certificate expired on {certificate.NotAfter:yyyy-MM-dd}.");
                    return false;
                }

                if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                {
                    ModelState.AddModelError(NopCertFieldKey,
                        $"The certificate is not valid yet (valid from {certificate.NotBefore:yyyy-MM-dd}).");
                    return false;
                }

                var identity = Services.Confirmation.Nop.NopIdentity.FromCertificate(certificate);
                if (identity is null)
                {
                    ModelState.AddModelError(NopCertFieldKey,
                        "The certificate subject does not look like an eKasa cash-register certificate (expected CN \"VATSK-... POKLADNICA ...\").");
                    return false;
                }

                settings.NopVatsk = identity.Vatsk;
                settings.NopPokladnica = identity.Pokladnica;
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(NopCertFieldKey, $"Could not load the certificate: {ex.Message}");
                return false;
            }
        }

        _configService.ApplyCredentials(settings, updated);
        return true;
    }

    private static async Task<string> ReadFormFileAsync(Microsoft.AspNetCore.Http.IFormFile file)
    {
        using var reader = new System.IO.StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }

    [HttpPost("test-backend")]
    public async Task<IActionResult> TestBackend(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _configService.GetSettingsAsync(storeId);
        if (settings is null)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Warning,
                Message = "Save the settings first.",
            });
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        if (!_confirmationSources.TryGetValue(settings.ConfirmationBackend, out var source))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = $"Unknown confirmation backend '{settings.ConfirmationBackend}'.",
            });
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var result = await source.TestAsync(settings, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = result.Ok ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Error,
            Message = result.Message ?? (result.Ok ? "Backend test passed." : "Backend test failed."),
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    /// <summary>
    /// ManualConfirm: the merchant verified the transfer in their banking
    /// app. Settles the invoice through the shared matching path so the
    /// normal BTCPay lifecycle (webhooks, POS paid screen) runs.
    /// </summary>
    [HttpPost("confirm/{reference}")]
    public async Task<IActionResult> ConfirmManually(string storeId, string reference, CancellationToken cancellationToken)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var request = await ctx.SepaPaymentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Reference == reference && r.StoreId == storeId, cancellationToken);
        if (request is null)
            return NotFound();

        var settings = await _configService.GetSettingsAsync(storeId, cancellationToken);
        var outcome = await _matchingService.ProcessAsync(
            $"manual:{User.Identity?.Name ?? "unknown"}",
            new ConfirmedPayment(reference, request.AmountDue, request.Currency, RawJson: null, DedupKey: null),
            settings?.AmountTolerance ?? 0m,
            cancellationToken);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = outcome == MatchOutcome.Settled
                ? StatusMessageModel.StatusSeverity.Success
                : StatusMessageModel.StatusSeverity.Warning,
            Message = outcome switch
            {
                MatchOutcome.Settled => $"Payment {reference} marked as paid.",
                MatchOutcome.Duplicate => $"Payment {reference} was already confirmed.",
                _ => $"Payment {reference} could not be settled ({outcome}).",
            },
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    private async Task<SepaSettingsPageViewModel> BuildPageModelAsync(string storeId)
    {
        var settings = await _configService.GetSettingsAsync(storeId);
        var model = new SepaSettingsPageViewModel
        {
            StoreId = storeId,
            Settings = settings is null
                ? new SepaSettingsViewModel()
                : new SepaSettingsViewModel
                {
                    Enabled = settings.Enabled,
                    CountryProfile = settings.CountryProfile,
                    Iban = settings.Iban,
                    Beneficiary = settings.Beneficiary,
                    Bic = settings.Bic,
                    Message = settings.Message,
                    ConfirmationBackend = settings.ConfirmationBackend,
                    AmountTolerance = settings.AmountTolerance,
                    NopEnvironment = _configService.GetCredentials(settings).NopEnvironment,
                    NopCertSet = _configService.GetCredentials(settings).HasNopCertificate,
                    NopVatsk = settings.NopVatsk,
                    NopPokladnica = settings.NopPokladnica,
                },
        };

        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var rows = await ctx.SepaPaymentRequests
                .AsNoTracking()
                .Where(r => r.StoreId == storeId &&
                            (r.State == SepaPaymentRequestState.Pending || r.State == SepaPaymentRequestState.ManualReview))
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync();

            foreach (var row in rows)
            {
                var vm = new SepaPendingRowViewModel
                {
                    Reference = row.Reference,
                    InvoiceId = row.InvoiceId,
                    State = row.State,
                    AmountDue = row.AmountDue,
                    CreatedAt = row.CreatedAt,
                    ReviewReason = row.ReviewReason,
                };
                if (row.State == SepaPaymentRequestState.ManualReview)
                    model.ReviewRequests.Add(vm);
                else
                    model.PendingRequests.Add(vm);
            }
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Tables not migrated yet - show empty lists.
        }

        return model;
    }
}
