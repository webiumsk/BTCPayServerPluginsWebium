#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;

/// <summary>
/// Fio banka token API (Fio API Bankovnictví v1.9, fio.cz/docs). Two
/// endpoints are used:
/// - GET /v1/rest/last/{token}/transactions.json - movements since the
///   server-side cursor ("zarážka"); the cursor advances automatically on
///   every non-empty response, so this client must be the token's only
///   consumer.
/// - GET /v1/rest/periods/{token}/{from}/{to}/transactions.json - used by
///   the settings Test button (does not move the cursor).
/// The bank enforces a minimum of 30 seconds between requests per token
/// (HTTP 409 otherwise) - the polling service ticks at 60 s.
/// </summary>
public class FioApiClient
{
    public const string BaseUrl = "https://fioapi.fio.cz/v1/rest";

    private readonly IHttpClientFactory _httpClientFactory;

    public FioApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<JsonDocument?> GetLastTransactionsAsync(string token, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(FioApiClient));
        using var response = await client.GetAsync(
            $"{BaseUrl}/last/{Uri.EscapeDataString(token)}/transactions.json", cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return null; // 30 s rate limit hit - the next tick catches up

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(payload);
    }

    /// <summary>Cursor-free probe for the settings Test button.</summary>
    public async Task<JsonDocument> GetTodayTransactionsAsync(string token, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(FioApiClient));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var response = await client.GetAsync(
            $"{BaseUrl}/periods/{Uri.EscapeDataString(token)}/{today}/{today}/transactions.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(payload);
    }
}
