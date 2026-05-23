#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Sets <see cref="RaffleStringLocalizer.UiCultureItemKey"/> from the store checkout default language
/// before public raffle views render.
/// </summary>
public sealed class RaffleUiCultureFilter : IAsyncActionFilter
{
    private readonly RaffleService _raffle;
    private readonly StoreRepository _storeRepo;

    public RaffleUiCultureFilter(RaffleService raffle, StoreRepository storeRepo)
    {
        _raffle = raffle;
        _storeRepo = storeRepo;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.Query.TryGetValue("lang", out var langQuery)
            && RaffleStringLocalizer.NormalizeLanguageCode(langQuery.ToString()) is not null)
        {
            await next();
            return;
        }

        string? storeId = await ResolveStoreIdAsync(context);
        if (storeId is not null)
        {
            var store = await _storeRepo.FindStore(storeId);
            var defaultLang = store?.GetStoreBlob()?.DefaultLang;
            var normalized = RaffleStringLocalizer.NormalizeLanguageCode(defaultLang);
            if (normalized is not null)
                http.Items[RaffleStringLocalizer.UiCultureItemKey] = normalized;
        }

        await next();
    }

    private async Task<string?> ResolveStoreIdAsync(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("raffleId", out var raffleArg) && raffleArg is Guid raffleId)
        {
            var raffle = await _raffle.GetRaffleAsync(raffleId);
            return raffle?.StoreId;
        }

        if (context.ActionArguments.TryGetValue("invoiceId", out var invoiceArg)
            && invoiceArg is string invoiceId
            && !string.IsNullOrWhiteSpace(invoiceId))
        {
            var (raffle, _) = await _raffle.GetReceiptAsync(invoiceId);
            return raffle?.StoreId;
        }

        if (context.ActionArguments.TryGetValue("ticketId", out var ticketArg) && ticketArg is Guid ticketId)
        {
            var (_, raffle) = await _raffle.GetTicketWithDetailsAsync(ticketId);
            return raffle?.StoreId;
        }

        return null;
    }
}
