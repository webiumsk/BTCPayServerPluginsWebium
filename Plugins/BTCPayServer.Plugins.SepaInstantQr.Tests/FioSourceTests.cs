using System.Net;
using System.Net.Http;
using System.Text;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// Diagnostic mapping of the Fio Test button: the documented error states
/// (500 = nonexistent/inactive token, 409 = 30 s rate limit, 422 = 90-day
/// history lock) plus the measured ~30 s stall for invalid tokens, which
/// the source caps with its own timeout and must not confuse with caller
/// cancellation.
/// </summary>
public class FioSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(cancellationToken);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (FioSource Source, SepaStoreSettings Settings) CreateSource(
        Func<CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var config = new SepaConfigService(
            new SepaDbContextFactory(Options.Create(new DatabaseOptions())),
            new EphemeralDataProtectionProvider());
        var settings = new SepaStoreSettings { StoreId = "store" };
        config.ApplyCredentials(settings, new SepaBackendCredentials { FioToken = new string('a', 64) });

        var client = new FioApiClient(new StubHttpClientFactory(new StubHandler(respond)));
        return (new FioSource(client, config), settings);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Reports_the_account_on_success()
    {
        var (source, settings) = CreateSource(_ => Task.FromResult(Response(HttpStatusCode.OK,
            "{\"accountStatement\":{\"info\":{\"iban\":\"SK6883300000002600000000\",\"currency\":\"EUR\"},\"transactionList\":{\"transaction\":[]}}}")));

        var result = await source.TestAsync(settings, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("SK6883300000002600000000", result.Message);
    }

    [Fact]
    public async Task Local_timeout_maps_to_the_inactive_token_explanation()
    {
        var (source, settings) = CreateSource(async ct =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        source.TestTimeout = TimeSpan.FromMilliseconds(100);

        var result = await source.TestAsync(settings, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("5 minutes", result.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var (source, settings) = CreateSource(async ct =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.TestAsync(settings, caller.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "inactive")]
    [InlineData(HttpStatusCode.Conflict, "30 seconds")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "90 days")]
    public async Task Documented_error_states_get_friendly_messages(HttpStatusCode status, string fragment)
    {
        var (source, settings) = CreateSource(_ => Task.FromResult(Response(status)));

        var result = await source.TestAsync(settings, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(fragment, result.Message);
    }

    [Fact]
    public async Task Unknown_status_falls_back_to_the_exception_message()
    {
        var (source, settings) = CreateSource(_ => Task.FromResult(Response((HttpStatusCode)418)));

        var result = await source.TestAsync(settings, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("Fio API test failed:", result.Message);
        Assert.Contains("418", result.Message);
    }

    [Fact]
    public async Task Missing_token_short_circuits()
    {
        var config = new SepaConfigService(
            new SepaDbContextFactory(Options.Create(new DatabaseOptions())),
            new EphemeralDataProtectionProvider());
        var settings = new SepaStoreSettings { StoreId = "store" };
        var source = new FioSource(
            new FioApiClient(new StubHttpClientFactory(new StubHandler(
                _ => throw new InvalidOperationException("must not be called")))),
            config);

        var result = await source.TestAsync(settings, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("No Fio API token", result.Message);
    }
}
