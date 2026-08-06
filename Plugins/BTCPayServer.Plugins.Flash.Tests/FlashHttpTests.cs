using System;
using System.Net;
using BTCPayServer.Plugins.Flash;
using Xunit;

namespace BTCPayServer.Plugins.Flash.Tests;

public class FlashHttpTests
{
    [Theory]
    [InlineData("https://flashapp.me/.well-known/lnurlp/alice")]
    [InlineData("https://flashapp.me/.well-known/lnurlverify/SparkLightningReceiveRequest:abc")]
    [InlineData("https://sub.example.org/verify/x?y=1")]
    public void Accepts_public_https_default_port_dns_hosts(string url)
    {
        Assert.True(FlashHttp.IsSafeUrl(new Uri(url), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("http://flashapp.me/verify/x")]           // not https
    [InlineData("https://user:pw@flashapp.me/verify/x")]  // userinfo
    [InlineData("https://flashapp.me:8443/verify/x")]     // non-default port
    [InlineData("https://192.168.1.10/verify/x")]                // IPv4 literal
    [InlineData("https://[::1]/verify/x")]                       // IPv6 literal
    [InlineData("ftp://flashapp.me/verify/x")]            // wrong scheme
    public void Rejects_unsafe_urls(string url)
    {
        Assert.False(FlashHttp.IsSafeUrl(new Uri(url), out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]     // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]      // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]        // link-local
    [InlineData("fd00::1")]        // unique local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    public void Blocks_private_and_reserved_addresses(string ip)
        => Assert.True(FlashHttp.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]     // just outside 172.16/12
    [InlineData("2606:4700:4700::1111")]
    public void Allows_public_addresses(string ip)
        => Assert.False(FlashHttp.IsBlockedAddress(IPAddress.Parse(ip)));
}
