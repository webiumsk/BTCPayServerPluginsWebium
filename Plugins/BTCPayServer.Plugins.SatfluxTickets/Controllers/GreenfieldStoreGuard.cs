#nullable enable
using System;
using BTCPayServer.Abstractions.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.SatfluxTickets.Controllers;

internal static class GreenfieldStoreGuard
{
    public static IActionResult? RequireStore(HttpContext httpContext, ControllerBase controller, string storeId)
    {
        var store = httpContext.GetStoreData();
        if (store is null || !string.Equals(store.Id, storeId, StringComparison.Ordinal))
            return controller.CreateAPIError(404, "store-not-found", "The store was not found");
        return null;
    }
}
