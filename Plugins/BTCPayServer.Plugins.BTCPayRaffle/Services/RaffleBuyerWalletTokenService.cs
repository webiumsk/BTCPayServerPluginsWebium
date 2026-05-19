#nullable enable
using System;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Signed links for the buyer wallet (<c>/raffle/{id}/my</c>) — all tickets for one email on a raffle.
/// </summary>
public class RaffleBuyerWalletTokenService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);
    private const string Separator = "|";
    private readonly IDataProtector _protector;

    public RaffleBuyerWalletTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.BTCPayRaffle.BuyerWallet.v1");
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateToken(Guid raffleId, string buyerEmail, TimeSpan? lifetime = null)
    {
        var normalized = RaffleBuyerEmail.Normalize(buyerEmail)
            ?? throw new ArgumentException("Buyer email is required", nameof(buyerEmail));

        lifetime ??= DefaultLifetime;
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime.Value);
        var payload = string.Join(Separator,
            raffleId.ToString("N"),
            normalized,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return (_protector.Protect(payload), expiresAt);
    }

    public bool TryValidate(string? token, Guid raffleId, out string normalizedEmail, out DateTimeOffset expiresAt)
    {
        normalizedEmail = "";
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

        normalizedEmail = parts[1];
        if (string.IsNullOrEmpty(normalizedEmail)) return false;

        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            return false;

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        return expiresAt > DateTimeOffset.UtcNow;
    }
}
