using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.LnAddressConnect;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class LnAddressClientTests
{
    static LnAddressLightningClient Client()
    {
        var resolved = new ResolvedLnAddress(
            new Uri("https://flashapp.me/.well-known/lnurlp/alice"), "alice@flashapp.me", "flashapp.me");
        return new LnAddressLightningClient(resolved, Network.Main, new FakeHttp().Client(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Pay_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Client().Pay("lnbc1", TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetBalance_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Client().GetBalance(TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetInfo_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => Client().GetInfo(TestContext.Current.CancellationToken));

    [Fact]
    public void DisplayName_and_ServerUri_present()
    {
        var c = Client();
        // Curated domain -> brand name; the fixture address is at flashapp.me.
        Assert.Equal("Flash", c.DisplayName);
        Assert.Equal("https://flashapp.me/", c.ServerUri!.ToString());
    }

    [Fact]
    public async Task ListPayments_is_empty_and_GetPayment_is_null()
    {
        Assert.Empty(await Client().ListPayments(TestContext.Current.CancellationToken));
        Assert.Null(await Client().GetPayment(new string('e', 64), TestContext.Current.CancellationToken));
    }
}
