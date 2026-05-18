using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public sealed class CashuMeltMintClientPollTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend!(request, cancellationToken);
    }

    [Fact]
    public async Task GetMintQuoteForPollAsync_429_ReturnsTransient_NoException()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { { "Retry-After", "7" } }
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.GetMintQuoteForPollAsync("https://mint.example", "quote-abc", default);

        Assert.False(r.Success);
        Assert.True(r.TransientFailure);
        Assert.Equal(7, r.RetryAfterSeconds);
        Assert.Null(r.Quote);
    }

    [Fact]
    public async Task GetMintQuoteForPollAsync_500_ReturnsTransient_NoException()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.GetMintQuoteForPollAsync("https://mint.example", "quote-xyz", default);

        Assert.False(r.Success);
        Assert.True(r.TransientFailure);
        Assert.Null(r.Quote);
    }

    [Fact]
    public async Task GetMintQuoteForPollAsync_NetworkUnreachable_ReturnsTransient_NoThrow()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => throw new HttpRequestException(
                "Network is unreachable (mint.minibits.cash:443)",
                new SocketException((int)SocketError.NetworkUnreachable))
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.GetMintQuoteForPollAsync("https://mint.minibits.cash", "quote-net", default);

        Assert.False(r.Success);
        Assert.True(r.TransientFailure);
        Assert.Equal(5, r.RetryAfterSeconds);
        Assert.Null(r.Quote);
    }

    [Fact]
    public async Task GetMintQuoteForPollAsync_200_ReturnsQuote()
    {
        var payload = new
        {
            quote = "q1",
            request = "lnbc1fake",
            amount = 1000L,
            unit = "sat",
            state = "PAID",
            expiry = (long?)null
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.GetMintQuoteForPollAsync("https://mint.example", "q1", default);

        Assert.True(r.Success);
        Assert.False(r.TransientFailure);
        Assert.NotNull(r.Quote);
        Assert.Equal("PAID", r.Quote!.State);
        Assert.Equal("q1", r.Quote.Quote);
    }
}
