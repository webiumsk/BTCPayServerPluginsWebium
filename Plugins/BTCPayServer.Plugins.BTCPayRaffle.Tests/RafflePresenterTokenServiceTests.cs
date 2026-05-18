using System;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RafflePresenterTokenServiceTests
{
    private static RafflePresenterTokenService CreateService() =>
        new(new EphemeralDataProtectionProvider());

    [Fact]
    public void CreateToken_ValidatesForMatchingRaffleAndStore()
    {
        var svc = CreateService();
        var raffleId = Guid.NewGuid();
        const string storeId = "store-abc";

        var (token, expiresAt) = svc.CreateToken(raffleId, storeId, TimeSpan.FromHours(1));

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
        Assert.True(svc.TryValidate(token, raffleId, storeId, out var parsed));
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryValidate_RejectsWrongRaffleOrStore()
    {
        var svc = CreateService();
        var raffleId = Guid.NewGuid();
        var (token, _) = svc.CreateToken(raffleId, "store-a");

        Assert.False(svc.TryValidate(token, Guid.NewGuid(), "store-a", out _));
        Assert.False(svc.TryValidate(token, raffleId, "store-b", out _));
        Assert.False(svc.TryValidate(null, raffleId, "store-a", out _));
    }

    [Fact]
    public void TryValidate_RejectsExpiredToken()
    {
        var svc = CreateService();
        var raffleId = Guid.NewGuid();
        var (token, _) = svc.CreateToken(raffleId, "store-a", TimeSpan.FromMilliseconds(-1));

        Assert.False(svc.TryValidate(token, raffleId, "store-a", out _));
    }
}
