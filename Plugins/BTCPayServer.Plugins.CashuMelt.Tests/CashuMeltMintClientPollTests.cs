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

    [Fact]
    public async Task MeltTokensAsync_ModernNut05Payload_ParsesStateAndPreimage()
    {
        // Regression: newer mints omit the legacy "paid" bool and send "state" +
        // "payment_preimage"; a successful melt was misread as unpaid.
        const string json = """
            {"quote":"mq1","amount":17878,"fee_reserve":180,"state":"PAID",
             "expiry":1754400000,"payment_preimage":"abcd1234","change":[]}
            """;
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.MeltTokensAsync("https://mint.example", "mq1",
            Array.Empty<CashuMeltMintClient.CashuMeltProof>(), default);

        Assert.NotNull(r);
        Assert.Equal("PAID", r!.State);
        Assert.Equal("abcd1234", r.PaymentPreimage);
        Assert.False(r.Paid); // legacy field absent - state must be authoritative
    }

    [Fact]
    public async Task MeltTokensAsync_LegacyPaidPayload_StillParses()
    {
        const string json = """{"paid":true,"proof":"ef567890"}""";
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.MeltTokensAsync("https://mint.example", "mq1",
            Array.Empty<CashuMeltMintClient.CashuMeltProof>(), default);

        Assert.NotNull(r);
        Assert.True(r!.Paid);
        Assert.Equal("ef567890", r.Proof);
        Assert.Null(r.State);
    }

    [Fact]
    public async Task MeltTokensAsync_AlreadySpent400_ThrowsTypedProtocolException()
    {
        // Regression: "proofs already spent" (11001) surfaced as a generic transient HTTP
        // error, so the settlement retried a doomed melt forever instead of reconciling.
        const string json = """{"detail":"proofs already spent","code":11001}""";
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var ex = await Assert.ThrowsAsync<BTCPayServer.Plugins.CashuMelt.Errors.CashuMeltMintProtocolException>(
            () => sut.MeltTokensAsync("https://mint.example", "mq1",
                Array.Empty<CashuMeltMintClient.CashuMeltProof>(), default));

        Assert.Equal(11001, ex.MintErrorCode);
        Assert.Equal("proofs already spent", ex.Detail);
    }

    [Fact]
    public async Task MeltTokensAsync_NonMintError400_ThrowsPlainHttpRequestException()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("not json", Encoding.UTF8, "text/plain")
            })
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.MeltTokensAsync("https://mint.example", "mq1",
                Array.Empty<CashuMeltMintClient.CashuMeltProof>(), default));

        Assert.IsNotType<BTCPayServer.Plugins.CashuMelt.Errors.CashuMeltMintProtocolException>(ex);
    }

    [Fact]
    public async Task GetMeltQuoteAsync_ReturnsStateAndPreimage()
    {
        const string json = """
            {"quote":"mq2","amount":17878,"fee_reserve":180,"state":"PAID",
             "expiry":1754400000,"payment_preimage":"beef"}
            """;
        var handler = new StubHandler
        {
            OnSend = (req, _) =>
            {
                Assert.EndsWith("/v1/melt/quote/bolt11/mq2", req.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        };
        using var http = new HttpClient(handler);
        var sut = new CashuMeltMintClient(http, NullLogger<CashuMeltMintClient>.Instance);

        var r = await sut.GetMeltQuoteAsync("https://mint.example", "mq2", default);

        Assert.NotNull(r);
        Assert.Equal("PAID", r!.State);
        Assert.Equal("beef", r.PaymentPreimage);
        Assert.Equal(17878, r.Amount);
    }
}
