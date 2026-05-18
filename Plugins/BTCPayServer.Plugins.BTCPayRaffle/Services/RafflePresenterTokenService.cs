#nullable enable
using System;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Issues short-lived presenter tokens for the public draw UI without BTCPay user login.
/// Minted via Greenfield; validated on <c>/raffle/{id}/present</c> routes only.
/// </summary>
public class RafflePresenterTokenService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(4);
    private const string Separator = "|";
    private readonly IDataProtector _protector;

    public RafflePresenterTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.BTCPayRaffle.Presenter.v1");
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateToken(Guid raffleId, string storeId, TimeSpan? lifetime = null)
    {
        lifetime ??= DefaultLifetime;
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime.Value);
        var payload = string.Join(Separator, raffleId.ToString("N"), storeId, expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return (_protector.Protect(payload), expiresAt);
    }

    public bool TryValidate(string? token, Guid raffleId, string storeId, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch
        {
            return false;
        }

        var parts = payload.Split(Separator);
        if (parts.Length != 3) return false;
        if (!Guid.TryParseExact(parts[0], "N", out var tokenRaffleId) || tokenRaffleId != raffleId)
            return false;
        if (!string.Equals(parts[1], storeId, StringComparison.Ordinal))
            return false;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return false;

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        return expiresAt > DateTimeOffset.UtcNow;
    }
}
