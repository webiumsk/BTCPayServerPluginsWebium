#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>HttpOnly cookie for buyer wallet after one-time token redemption.</summary>
public static class RaffleBuyerWalletCookie
{
    public static string Name(Guid raffleId) => $"btcpay_raffle_wallet_{raffleId:N}";

    public static void Set(HttpResponse response, Guid raffleId, string token, DateTimeOffset expiresAt, bool secure)
    {
        response.Cookies.Append(Name(raffleId), token, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = $"/raffle/{raffleId}",
            Expires = expiresAt
        });
    }

    public static string? Get(HttpRequest request, Guid raffleId) =>
        request.Cookies.TryGetValue(Name(raffleId), out var value) ? value : null;
}
