using System.Net;
using System.Net.Http;
using System.Text;
using BTCPayServer.Plugins.SepaInstantQr.Models;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// Public NOP diagnostics client ("Kde je moja platba"): response mapping
/// per observed behaviour of kdejemojaplatba.kverkom.sk (2026-09-16) -
/// RFC 7807 404/400 bodies, plus the failure modes the caller must be able
/// to show (rate limit, WAF 403, timeout). Nothing here ever throws.
/// </summary>
public class NopPublicClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _respond(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private const string Id = "QR-5178ebd792314ed599e2d9549828968e";

    private static (NopPublicClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        return (new NopPublicClient(new StubHttpClientFactory(handler), NullLogger<NopPublicClient>.Instance), handler);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void Environment_selects_the_official_hosts()
    {
        Assert.Equal("https://kdejemojaplatba.kverkom.sk/api/v1/getTransactionHistory/" + Id, NopPublicClient.HistoryUrlFor("PROD", Id));
        Assert.Equal("https://kdejemojaplatba-i.kverkom.sk/api/v1/getTransactionHistory/" + Id, NopPublicClient.HistoryUrlFor("INT", Id));
        Assert.Equal("https://kdejemojaplatba-i.kverkom.sk/api/v1/getTransactionHistory/" + Id, NopPublicClient.HistoryUrlFor(null, Id));
    }

    [Theory]
    [InlineData("QR-5178ebd792314ed599e2d9549828968e", true)]
    [InlineData("QR-5178EBD792314ED599E2D9549828968E", true)]
    [InlineData("QR-5178ebd792314ed599e2d954982896", false)]
    [InlineData("1234567890", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_nop_shaped_references_are_looked_up(string? reference, bool expected)
        => Assert.Equal(expected, PaymentReferenceGenerator.IsNopShaped(reference));

    [Fact]
    public async Task Invalid_reference_never_hits_the_network()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("must not be called"));

        var result = await client.GetTransactionHistoryAsync("PROD", "1234567890", CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.InvalidId, result.Status);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Maps_a_found_transaction_with_payment_and_timeline()
    {
        const string body = """
            {"transactionId":"QR-5178ebd792314ed599e2d9549828968e","createdAt":"2026-09-16T08:00:00Z",
             "indexedAt":"2026-09-16T08:01:10Z","matchedAt":"2026-09-16T08:01:11Z","publishedAt":null,
             "organizationName":"Kaviareň s.r.o.","status":"MATCHED","payment":{"amount":"12.50","currency":"eur"}}
            """;
        var (client, handler) = Create((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.Found, result.Status);
        Assert.Equal(200, result.HttpStatus);
        var h = Assert.IsType<NopTransactionHistory>(result.History);
        Assert.Equal(Id, h.TransactionId);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero), h.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 8, 1, 10, TimeSpan.Zero), h.IndexedAt);
        Assert.NotNull(h.MatchedAt);
        Assert.Null(h.PublishedAt);
        Assert.Null(h.ReceivedAt);
        Assert.Equal("Kaviareň s.r.o.", h.OrganizationName);
        Assert.Equal("MATCHED", h.Status);
        Assert.Equal(12.50m, h.Amount);
        Assert.Equal("EUR", h.Currency);
        Assert.Equal(body, h.RawJson);
        Assert.Equal("https://kdejemojaplatba.kverkom.sk/api/v1/getTransactionHistory/" + Id, handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("BTCPayServer.Plugins.SepaInstantQr/", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public void Accepts_snake_case_and_transactionAmount_spellings()
    {
        var h = NopPublicClient.Parse(
            """{"transaction_id":"QR-5178ebd792314ed599e2d9549828968e","created_at":"2026-09-16T08:00:00.000Z","transactionAmount":{"amount":3,"currency":"EUR"}}""");

        Assert.NotNull(h);
        Assert.Equal(Id, h!.TransactionId);
        Assert.NotNull(h.CreatedAt);
        Assert.Equal(3m, h.Amount);
        Assert.Equal("EUR", h.Currency);
    }

    [Fact]
    public void Non_object_payload_is_rejected()
        => Assert.Null(NopPublicClient.Parse("[]"));

    [Fact]
    public async Task Not_found_carries_the_problem_detail()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Response(HttpStatusCode.NotFound,
            """{"title":"Not found","type":null,"detail":"Transaction not found","trace":null}""")));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.NotFound, result.Status);
        Assert.Equal("Transaction not found", result.Message);
        Assert.Null(result.History);
    }

    [Fact]
    public async Task Bad_request_from_nop_maps_to_invalid_id()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Response(HttpStatusCode.BadRequest,
            """{"title":"Bad request","type":null,"detail":"Invalid URL parameter","trace":null}""")));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.InvalidId, result.Status);
        Assert.Equal("Invalid URL parameter", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task Service_problems_are_unavailable_not_exceptions(HttpStatusCode status, string expectedFragment)
    {
        var (client, _) = Create((_, _) => Task.FromResult(Response(status, "Forbidden")));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.Unavailable, result.Status);
        Assert.Contains(expectedFragment, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((int)status, result.HttpStatus);
    }

    [Fact]
    public async Task Unexpected_ok_payload_is_unavailable()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "not json")));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Network_failure_is_unavailable()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("dns"));

        var result = await client.GetTransactionHistoryAsync("PROD", Id, CancellationToken.None);

        Assert.Equal(NopPublicLookupStatus.Unavailable, result.Status);
        Assert.Null(result.HttpStatus);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Create(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetTransactionHistoryAsync("PROD", Id, cts.Token));
    }

    private const string NotFoundBody = "{\"detail\":\"Transaction not found\"}";

    [Fact]
    public async Task Store_without_certificate_asks_prod_only()
    {
        var calls = new List<string>();
        var (client, _) = Create((req, _) =>
        {
            calls.Add(req.RequestUri!.Host);
            return Task.FromResult(Response(HttpStatusCode.NotFound, NotFoundBody));
        });

        var (environment, result) = await client.LookupForStoreAsync(
            new SepaBackendCredentials { NopEnvironment = "INT" }, Id, CancellationToken.None);

        Assert.Equal("PROD", environment);
        Assert.Equal(NopPublicLookupStatus.NotFound, result.Status);
        Assert.Equal(["kdejemojaplatba.kverkom.sk"], calls);
    }

    [Fact]
    public async Task Store_with_certificate_falls_back_to_the_other_environment_on_not_found()
    {
        var calls = new List<string>();
        var (client, _) = Create((req, _) =>
        {
            calls.Add(req.RequestUri!.Host);
            return Task.FromResult(req.RequestUri.Host.StartsWith("kdejemojaplatba-i", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, "{\"transactionId\":\"" + Id + "\"}")
                : Response(HttpStatusCode.NotFound, NotFoundBody));
        });
        var credentials = new SepaBackendCredentials { NopEnvironment = "PROD", NopPfxBase64 = "dummy" };
        Assert.True(credentials.HasNopCertificate);

        var (environment, result) = await client.LookupForStoreAsync(credentials, Id, CancellationToken.None);

        Assert.Equal("INT", environment);
        Assert.Equal(NopPublicLookupStatus.Found, result.Status);
        Assert.Equal(["kdejemojaplatba.kverkom.sk", "kdejemojaplatba-i.kverkom.sk"], calls);
    }

    [Fact]
    public async Task Fallback_that_also_misses_reports_the_primary_environment()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Response(HttpStatusCode.NotFound, "{}")));
        var credentials = new SepaBackendCredentials { NopEnvironment = "INT", NopPfxBase64 = "dummy" };

        var (environment, result) = await client.LookupForStoreAsync(credentials, Id, CancellationToken.None);

        Assert.Equal("INT", environment);
        Assert.Equal(NopPublicLookupStatus.NotFound, result.Status);
    }

    [Fact]
    public void Api_data_mapping_uses_the_effective_environment_and_status_names()
    {
        var found = SepaNopHistoryData.From(Id, "prod",
            new NopPublicLookupResult(NopPublicLookupStatus.Found,
                new NopTransactionHistory { TransactionId = Id, Amount = 1m, Currency = "EUR", RawJson = "{}" }, null, 200));
        Assert.Equal("found", found.Status);
        Assert.Equal("PROD", found.Environment);
        Assert.Equal(1m, found.Amount);
        Assert.Equal("{}", found.Raw);

        var missing = SepaNopHistoryData.From(Id, null,
            new NopPublicLookupResult(NopPublicLookupStatus.NotFound, null, "Transaction not found", 404));
        Assert.Equal("not_found", missing.Status);
        Assert.Equal("INT", missing.Environment);
        Assert.Null(missing.Raw);
    }
}
