#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Models;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
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

    public UICashuMeltController(
        StoreRepository storeRepository,
        CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory,
        CashuMeltPaymentService paymentService,
        PaymentMethodHandlerDictionary handlers)
    {
        _storeRepository = storeRepository;
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _paymentService = paymentService;
        _handlers = handlers;
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null) return NotFound();

        var settings = await _configService.GetSettingsAsync(storeId)
            ?? new CashuMeltStoreSettings { StoreId = storeId, Unit = "sat" };

        return View(await BuildPageModelAsync(storeId, settings));
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
                    Message  = ex.Message
                });
                return View(await BuildPageModelAsync(storeId, model));
            }

            // Enable / disable the payment method on the store
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
                Message  = "CashuMelt settings saved"
            });

            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return View(await BuildPageModelAsync(storeId, model));
    }

    private async Task<CashuMeltSettingsPageModel> BuildPageModelAsync(string storeId, CashuMeltStoreSettings settings)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var rows = await ctx.CashuMeltPaymentRequests
            .AsNoTracking()
            .Where(r => r.StoreId == storeId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new
            {
                r.QuoteId,
                r.InvoiceId,
                r.AmountSats,
                r.State,
                r.SettlementState,
                r.SettlementError,
                r.CreatedAt,
                r.MintedProofsJson
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
                CanRetryForRow(r.SettlementState, r.MintedProofsJson)))
            .ToList();

        return new CashuMeltSettingsPageModel { Settings = settings, RecentPayments = recent };
    }

    private static bool CanRetryForRow(string settlementState, string? mintedProofsJson) =>
        settlementState != "SETTLED"
        && !(settlementState == "FAILED" && string.IsNullOrEmpty(mintedProofsJson));
}
