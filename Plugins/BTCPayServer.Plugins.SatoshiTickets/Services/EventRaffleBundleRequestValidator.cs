#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SatoshiTickets.Services.Integration;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.SatoshiTickets.Services;

public static class EventRaffleBundleRequestValidator
{
    public const int MaxTicketsPerAdmission = 20;

    public static async Task ApplyBundleFieldsAsync(
        ModelStateDictionary modelState,
        string storeId,
        int bundledTicketsPerAdmission,
        Guid? bundledRaffleId,
        IRaffleEventBundleClient? raffleBundle)
    {
        if (bundledTicketsPerAdmission < 0)
        {
            modelState.AddModelError(nameof(bundledTicketsPerAdmission),
                "Bundled raffle tickets per admission cannot be negative");
            return;
        }

        if (bundledTicketsPerAdmission > MaxTicketsPerAdmission)
        {
            modelState.AddModelError(nameof(bundledTicketsPerAdmission),
                $"Bundled raffle tickets per admission cannot exceed {MaxTicketsPerAdmission}");
            return;
        }

        if (bundledTicketsPerAdmission == 0)
            return;

        if (!bundledRaffleId.HasValue || bundledRaffleId.Value == Guid.Empty)
        {
            modelState.AddModelError(nameof(bundledRaffleId),
                "Select an open raffle when including raffle tickets per admission");
            return;
        }

        if (raffleBundle is null)
        {
            modelState.AddModelError(nameof(bundledRaffleId),
                "Ticket type raffle bundles require BTCPay Raffle plugin 1.3.1 or newer on this server. Upgrade the Raffle plugin, or set raffle tickets per admission to 0.");
            return;
        }

        var (ok, error) = await raffleBundle.ValidateBundledRaffleAsync(storeId, bundledRaffleId.Value);
        if (!ok)
            modelState.AddModelError(nameof(bundledRaffleId), error ?? "Invalid raffle");
    }

    public static async Task<string?> ValidateAsync(
        string storeId,
        int bundledTicketsPerAdmission,
        Guid? bundledRaffleId,
        IRaffleEventBundleClient? raffleBundle)
    {
        var modelState = new ModelStateDictionary();
        await ApplyBundleFieldsAsync(modelState, storeId, bundledTicketsPerAdmission, bundledRaffleId, raffleBundle);
        foreach (var entry in modelState)
        {
            var message = entry.Value.Errors.FirstOrDefault()?.ErrorMessage;
            if (!string.IsNullOrEmpty(message))
                return message;
        }
        return null;
    }
}
