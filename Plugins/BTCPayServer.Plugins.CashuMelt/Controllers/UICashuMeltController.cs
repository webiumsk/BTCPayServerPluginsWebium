#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Models;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/cashumelt")]
public class UICashuMeltController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly CashuMeltPaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly StoreLightningBackendService _backendService;
    private readonly CashuMeltLightningAddressValidator _lightningAddressValidator;

    public UICashuMeltController(
        StoreRepository storeRepository,
        CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory,
        CashuMeltPaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        StoreLightningBackendService backendService,
        CashuMeltLightningAddressValidator lightningAddressValidator)
    {
        _storeRepository = storeRepository;
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _paymentService = paymentService;
        _handlers = handlers;
        _backendService = backendService;
        _lightningAddressValidator = lightningAddressValidator;
    }

    private void PopulateLightningBackendViewData(StoreData store)
    {
        var info = _backendService.Detect(store);
        ViewData["CashuMeltBackendCanPayout"] = info.CanAttemptPayout;
        ViewData["CashuMeltBackendType"] = info.BackendType.ToString();
        ViewData["CashuMeltBackendDescription"] = info.Description;
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(
        string storeId,
        [FromQuery] string? settlement = null,
        [FromQuery] string? invoice = null,
        [FromQuery] string? export = null)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null) return NotFound();

        PopulateLightningBackendViewData(store);

        var settings = await _configService.GetSettingsAsync(storeId)
            ?? new CashuMeltStoreSettings { StoreId = storeId, Unit = "sat" };

        var page = await BuildPageModelAsync(storeId, settings, settlement, invoice, take: 200);

        if (string.Equals(export, "csv", StringComparison.OrdinalIgnoreCase))
            return ExportPaymentsCsv(storeId, page);

        return View(page);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(
        string storeId,
        [Bind(Prefix = "Settings")] CashuMeltStoreSettings? model,
        string command)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null) return NotFound();

        PopulateLightningBackendViewData(store);

        model ??= new CashuMeltStoreSettings { StoreId = storeId, Unit = "sat" };
        model.StoreId = storeId;

        if (command?.Equals("retry", StringComparison.OrdinalIgnoreCase) == true)
        {
            var quoteId = (Request.Form["quoteId"].ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(quoteId))
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Severity = StatusMessageModel.StatusSeverity.Error,
                    Message = "Missing quote id for retry."
                });
                return RedirectToAction(nameof(Settings), new { storeId });
            }

            var outcome = await _paymentService.RetrySettlementAsync(storeId, quoteId);
            switch (outcome.Kind)
            {
                case CashuMeltRetryKind.NotFound:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Error,
                        Message = "Payment row not found for this store."
                    });
                    break;
                case CashuMeltRetryKind.AlreadySettled:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Success,
                        Message = "This quote is already settled."
                    });
                    break;
                case CashuMeltRetryKind.CannotRetryMissingProofs:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Error,
                        Message =
                            "Cannot retry: stored proofs are missing (tokens may already be spent or were never minted). Reconcile with the mint using the quote id, or see RELEASE_NOTES."
                    });
                    break;
                case CashuMeltRetryKind.Completed when outcome.Settled:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Success,
                        Message = "Retry succeeded: settlement completed."
                    });
                    break;
                case CashuMeltRetryKind.Completed when outcome.RetryAfterSeconds is > 0:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Info,
                        Message =
                            $"Retry ran but settlement is still in progress. Wait ~{outcome.RetryAfterSeconds} seconds (mint backoff) and use Retry again, or wait for checkout poll."
                    });
                    break;
                case CashuMeltRetryKind.Completed:
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Warning,
                        Message = string.IsNullOrWhiteSpace(outcome.Error)
                            ? "Retry ran; settlement not complete yet. Check logs or try again shortly."
                            : outcome.Error
                    });
                    break;
            }

            return RedirectToAction(nameof(Settings), new { storeId });
        }

        if (command?.Equals("save", StringComparison.OrdinalIgnoreCase) == true)
        {
            ApplyOptionalFeeFormFields(model);

            if (model.Enabled)
            {
                model.MintUrl = model.MintUrl?.Trim().TrimEnd('/') ?? "";

                if (string.IsNullOrWhiteSpace(model.MintUrl))
                    ModelState.AddModelError(nameof(model.MintUrl), "Mint URL is required");
                else if (!model.MintUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    ModelState.AddModelError(nameof(model.MintUrl), "Mint URL must use HTTPS");

                model.Unit = "sat";

                if (string.IsNullOrWhiteSpace(model.LightningAddress))
                    ModelState.AddModelError(nameof(model.LightningAddress),
                        "Lightning address is required for automatic merchant payout");
                else if (!model.LightningAddress.Contains('@'))
                    ModelState.AddModelError(nameof(model.LightningAddress),
                        "Lightning address must be in the format user@domain");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(model);
                    CashuMeltSettingsValidation.ValidateOptionalFeeCaps(
                        model.MaxMeltFeeReserveSats,
                        model.MaxMeltFeeReservePercentOfMinted);
                }
                catch (InvalidOperationException ex)
                {
                    ModelState.AddModelError(string.Empty, ex.Message);
                }

                if (ModelState.IsValid && model.Enabled && !string.IsNullOrWhiteSpace(model.LightningAddress))
                {
                    try
                    {
                        await _lightningAddressValidator.ValidateForPayoutAsync(model.LightningAddress);
                    }
                    catch (InvalidOperationException ex)
                    {
                        ModelState.AddModelError(nameof(model.LightningAddress), ex.Message);
                    }
                }
            }

            if (!ModelState.IsValid)
                return View(await BuildPageModelAsync(storeId, model));

            try
            {
                await _configService.SaveSettingsAsync(model);
            }
            catch (InvalidOperationException ex)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Severity = StatusMessageModel.StatusSeverity.Error,
                    Message = ex.Message
                });
                return View(await BuildPageModelAsync(storeId, model));
            }

            if (_handlers.Support(CashuMeltPlugin.CashuMeltPaymentMethodId))
            {
                store.SetPaymentMethodConfig(
                    _handlers[CashuMeltPlugin.CashuMeltPaymentMethodId],
                    new CashuMeltPaymentMethodConfig { Enabled = model.Enabled });
                await _storeRepository.UpdateStore(store);
            }

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = "CashuMelt settings saved"
            });

            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return View(await BuildPageModelAsync(storeId, model));
    }

    private void ApplyOptionalFeeFormFields(CashuMeltStoreSettings model)
    {
        var satsRaw = Request.Form["maxFeeReserveSats"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(satsRaw))
            model.MaxMeltFeeReserveSats = null;
        else if (long.TryParse(satsRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sats))
            model.MaxMeltFeeReserveSats = sats;
        else
            ModelState.AddModelError("maxFeeReserveSats", "Max melt fee reserve (sats) must be a whole number or empty.");

        var pctRaw = Request.Form["maxFeePercent"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(pctRaw))
            model.MaxMeltFeeReservePercentOfMinted = null;
        else if (decimal.TryParse(pctRaw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
            model.MaxMeltFeeReservePercentOfMinted = pct;
        else
            ModelState.AddModelError("maxFeePercent", "Max melt fee percent must be a decimal number or empty.");
    }

    private IActionResult ExportPaymentsCsv(string storeId, CashuMeltSettingsPageModel page)
    {
        var sb = new StringBuilder();
        sb.AppendLine("quote_id,invoice_id,amount_sats,unit,mint_state,settlement_state,retry_count,needs_manual_review,failure_reason_code,created_utc,settlement_error,mint_quote_poll_url");
        foreach (var r in page.RecentPayments)
        {
            static string Csv(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "\"\"";
                return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            }

            sb.Append(Csv(r.QuoteId)).Append(',')
                .Append(Csv(r.InvoiceId)).Append(',')
                .Append(r.AmountSats.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv("sat")).Append(',')
                .Append(Csv(r.State)).Append(',')
                .Append(Csv(r.SettlementState)).Append(',')
                .Append(r.RetryCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(r.NeedsManualReview ? "true" : "false").Append(',')
                .Append(Csv(r.FailureReasonCode)).Append(',')
                .Append(Csv(r.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(r.SettlementError)).Append(',')
                .Append(Csv(r.MintQuotePollUrl))
                .AppendLine();
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8",
            $"cashumelt-payments-{storeId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private async Task<CashuMeltSettingsPageModel> BuildPageModelAsync(
        string storeId,
        CashuMeltStoreSettings settings,
        string? filterSettlement = null,
        string? filterInvoice = null,
        int take = 80)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var q = ctx.CashuMeltPaymentRequests
            .AsNoTracking()
            .Where(r => r.StoreId == storeId);

        if (!string.IsNullOrWhiteSpace(filterSettlement))
            q = q.Where(r => r.SettlementState == filterSettlement.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filterInvoice))
        {
            var inv = filterInvoice.Trim();
            q = q.Where(r => r.InvoiceId.Contains(inv));
        }

        var mintBase = CashuMeltMintPolicy.NormalizeMintUrl(settings.MintUrl);

        var rows = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(r => new
            {
                r.QuoteId,
                r.InvoiceId,
                r.AmountSats,
                r.State,
                r.SettlementState,
                r.SettlementError,
                r.CreatedAt,
                r.MintedProofsJson,
                r.Bolt11Invoice,
                r.NeedsManualReview,
                r.RetryCount,
                r.FailureReasonCode
            })
            .ToListAsync();

        var recent = rows
            .Select(r => new CashuMeltRecentPaymentRow(
                r.QuoteId,
                r.InvoiceId,
                r.AmountSats,
                r.State,
                r.SettlementState,
                r.SettlementError,
                r.CreatedAt,
                CanRetryForRow(r.SettlementState, r.MintedProofsJson),
                r.Bolt11Invoice,
                CashuMeltNutsUrls.MintQuoteBolt11PollUrl(mintBase, r.QuoteId),
                r.NeedsManualReview,
                r.RetryCount,
                r.FailureReasonCode))
            .ToList();

        var pendingChangeSat = await ctx.CashuMeltChangeProofs.AsNoTracking()
            .Where(p => p.StoreId == storeId && (p.State == "AVAILABLE" || p.State == "SWEEPING"))
            .SumAsync(p => (long?)p.Amount) ?? 0;
        var sweptChangeSat = await ctx.CashuMeltChangeProofs.AsNoTracking()
            .Where(p => p.StoreId == storeId && p.State == "SWEPT")
            .SumAsync(p => (long?)p.Amount) ?? 0;

        return new CashuMeltSettingsPageModel
        {
            Settings = settings,
            RecentPayments = recent,
            FilterSettlement = filterSettlement,
            FilterInvoice = filterInvoice,
            MintBaseNormalized = mintBase,
            PendingChangeSat = pendingChangeSat,
            SweptChangeSat = sweptChangeSat
        };
    }

    private static bool CanRetryForRow(string settlementState, string? mintedProofsJson) =>
        settlementState != "SETTLED"
        && !(settlementState == "FAILED" && string.IsNullOrEmpty(mintedProofsJson));
}
