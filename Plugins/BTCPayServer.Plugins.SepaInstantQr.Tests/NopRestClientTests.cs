using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class NopRestClientTests
{
    private const string CashRegister = "POKLADNICA-synthetic";
    private const string Path = "/v1/getAllTransactions/" + CashRegister;

    private sealed class FakeBank : HttpMessageHandler
    {
        private readonly Func<Uri, int, HttpResponseMessage> _respond;
        public List<Uri> Requests { get; } = new();
        public FakeBank(Func<Uri, int, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(_respond(uri, Requests.Count));
        }
    }

    private static HttpResponseMessage Page(string body, bool truncated = false, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-result-truncated", truncated ? "True" : "False");
        if (next is not null)
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        return response;
    }

    private static NopRestClient Client(FakeBank bank)
        => new(new HttpClient(bank, disposeHandler: false)
        {
            BaseAddress = new Uri(NopRestClient.BaseUrlFor("INT")),
        }, NullLogger.Instance);

    [Fact]
    public async Task Follows_documented_pages_and_preserves_an_older_identifier_with_a_recent_notification()
    {
        var continuation = "/api/v1/getAllTransactions/" + CashRegister + "?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000001";
        using var bank = new FakeBank((_, page) => page == 1
            ? Page("[{\"endToEndId\":\"synthetic-old-id\",\"idCreatedAt\":\"2026-10-01T00:00:00Z\",\"receivedAt\":\"2026-10-29T01:00:00Z\"}]", true, continuation)
            : Page("[{\"endToEndId\":\"synthetic-second-id\"}]"));
        using var client = Client(bank);

        var result = await client.GetAllTransactionsAsync(CashRegister, CancellationToken.None);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal("synthetic-old-id", result[0].GetProperty("endToEndId").GetString());
        Assert.Equal(2, bank.Requests.Count);
        Assert.Equal(string.Empty, bank.Requests[0].Query);
        Assert.Equal(Path, bank.Requests[0].AbsolutePath);
        Assert.Contains(bank.Requests[1].AbsolutePath, new[] { Path, "/api/v1/getAllTransactions/" + CashRegister });
    }

    [Theory]
    [InlineData("https://attacker.test/v1/getAllTransactions/POKLADNICA-synthetic?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002")]
    [InlineData("http://api-erp-i.kverkom.sk/v1/getAllTransactions/POKLADNICA-synthetic?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002")]
    [InlineData("https://user@api-erp-i.kverkom.sk/v1/getAllTransactions/POKLADNICA-synthetic?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002")]
    [InlineData("https://api-erp-i.kverkom.sk/v1/getAllTransactions/POKLADNICA-other?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002")]
    [InlineData("https://api-erp-i.kverkom.sk/v1/getAllTransactions/POKLADNICA-synthetic?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002#fragment")]
    [InlineData("https://api-erp-i.kverkom.sk:8443/v1/getAllTransactions/POKLADNICA-synthetic?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002")]
    public async Task Unsafe_continuations_fail_before_any_second_request(string next)
    {
        using var bank = new FakeBank((_, _) => Page("[]", true, next));
        using var client = Client(bank);

        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Truncated_page_without_a_continuation_is_not_a_successful_complete_batch()
    {
        using var bank = new FakeBank((_, _) => Page("[]", true));
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Cyclic_continuations_fail_without_unbounded_requests()
    {
        using var bank = new FakeBank((_, _) => Page("[]", true, Path + "?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000001"));
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.InRange(bank.Requests.Count, 1, 2);
    }

    [Fact]
    public async Task Multiple_next_links_are_ambiguous_and_fail_closed()
    {
        using var bank = new FakeBank((_, _) =>
        {
            var response = Page("[]", true, Path + "?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000001");
            response.Headers.TryAddWithoutValidation("Link", $"<{Path}?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-00000000000000000000000000000002>; rel=\"next\"");
            return response;
        });
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Page_limit_fails_closed_instead_of_silently_dropping_the_remainder()
    {
        using var bank = new FakeBank((_, page) => Page("[]", true, Path + "?date_from=2026-10-01T00%3A00%3A00Z&after_id=QR-" + page.ToString("x32")));
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.InRange(bank.Requests.Count, 1, 100);
    }

    [Fact]
    public async Task Notification_limit_fails_closed_instead_of_returning_a_partial_batch()
    {
        var body = JsonSerializer.Serialize(new int[10001]);
        using var bank = new FakeBank((_, _) => Page(body));
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Missing_completeness_header_cannot_be_treated_as_a_complete_batch()
    {
        using var bank = new FakeBank((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }

    [Fact]
    public async Task Non_array_payload_cannot_advance_a_successful_batch()
    {
        using var bank = new FakeBank((_, _) => Page("{}"));
        using var client = Client(bank);
        await Assert.ThrowsAsync<NopRestException>(() => client.GetAllTransactionsAsync(CashRegister, CancellationToken.None));
        Assert.Single(bank.Requests);
    }
}
