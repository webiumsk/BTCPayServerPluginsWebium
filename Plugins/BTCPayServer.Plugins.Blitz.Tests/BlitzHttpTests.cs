using System;
using System.Net;
using BTCPayServer.Plugins.Blitz;
using Xunit;

namespace BTCPayServer.Plugins.Blitz.Tests;

public class BlitzHttpTests
{
    [Theory]
    [InlineData("https://blitzwalletapp.com/.well-known/lnurlp/alice")]
    [InlineData("https://blitzwalletapp.com/.well-known/lnurlverify/SparkLightningReceiveRequest:abc")]
    [InlineData("https://sub.example.org/verify/x?y=1")]
    public void Accepts_public_https_default_port_dns_hosts(string url)
    {
        Assert.True(BlitzHttp.IsSafeUrl(new Uri(url), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("http://blitzwalletapp.com/verify/x")]           // not https
    [InlineData("https://user:pw@blitzwalletapp.com/verify/x")]  // userinfo
    [InlineData("https://blitzwalletapp.com:8443/verify/x")]     // non-default port
    [InlineData("https://192.168.1.10/verify/x")]                // IPv4 literal
    [InlineData("https://[::1]/verify/x")]                       // IPv6 literal
    [InlineData("ftp://blitzwalletapp.com/verify/x")]            // wrong scheme
    public void Rejects_unsafe_urls(string url)
    {
        Assert.False(BlitzHttp.IsSafeUrl(new Uri(url), out var reason));
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
        => Assert.True(BlitzHttp.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]     // just outside 172.16/12
    [InlineData("2606:4700:4700::1111")]
    public void Allows_public_addresses(string ip)
        => Assert.False(BlitzHttp.IsBlockedAddress(IPAddress.Parse(ip)));
}
