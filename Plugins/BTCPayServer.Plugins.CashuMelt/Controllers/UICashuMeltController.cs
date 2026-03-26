#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Payments;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.CashuMelt.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/cashumelt")]
public class UICashuMeltController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly CashuMeltConfigService _configService;
    private readonly PaymentMethodHandlerDictionary _handlers;

    public UICashuMeltController(
        StoreRepository storeRepository,
        CashuMeltConfigService configService,
        PaymentMethodHandlerDictionary handlers)
    {
        _storeRepository = storeRepository;
        _configService = configService;
        _handlers = handlers;
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null) return NotFound();

        var settings = await _configService.GetSettingsAsync(storeId)
            ?? new CashuMeltStoreSettings { StoreId = storeId, Unit = "sat" };

        return View(settings);
    }

    [HttpPost("")]
    public async Task<IActionResult> Settings(string storeId, CashuMeltStoreSettings model, string command)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null) return NotFound();

        model.StoreId = storeId;

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
                return View(model);

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
                return View(model);
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

        return View(model);
    }
}
