using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public class D21PanelSettlementProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<D21PanelSettlementProvider> logger) : ICashuMeltSettlementProvider
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<D21PanelSettlementProvider> _logger = logger;

    private readonly string? _baseUrl = configuration["CASHU_D21PANEL_BASE_URL"];
    private readonly string? _apiKey = configuration["CASHU_D21PANEL_API_KEY"];
    private readonly int _pollAttempts = int.TryParse(configuration["CASHU_D21PANEL_POLL_MAX_ATTEMPTS"], out var attempts) ? attempts : 5;
    private readonly int _pollDelayMs = int.TryParse(configuration["CASHU_D21PANEL_POLL_DELAY_MS"], out var delayMs) ? delayMs : 1500;

    public bool Supports(StoreLightningBackendInfo backendInfo)
    {
        return !string.IsNullOrWhiteSpace(_baseUrl)
               && !string.IsNullOrWhiteSpace(_apiKey)
               && backendInfo.CanAttemptPayout;
    }

    public async Task<CashuMeltSettlementResult> SettleAsync(
        StoreData store,
        StoreLightningBackendInfo backendInfo,
        CashuMeltStoreSettings settings,
        CashuMeltPaymentRequest paymentRequest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
            return new(false, "D21Panel settlement is not configured.");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-D21-Api-Key", _apiKey);

        var createPayload = new
        {
            idempotencyKey = $"{paymentRequest.InvoiceId}:{paymentRequest.QuoteId}",
            storeId = store.Id,
            invoiceId = paymentRequest.InvoiceId,
            quoteId = paymentRequest.QuoteId,
            mintUrl = settings.MintUrl,
            unit = paymentRequest.Unit,
            amount = paymentRequest.AmountSats,
            merchantLightningAddress = settings.LightningAddress,
            metadata = new
            {
                backend = backendInfo.BackendType.ToString(),
                btcpayPlugin = "BTCPayServer.Plugins.CashuMelt"
            }
        };

        var createJson = JsonSerializer.Serialize(createPayload);
        using var createContent = new StringContent(createJson, Encoding.UTF8, "application/json");
        using var createResp = await client.PostAsync("api/v1/cashumelt/settlements", createContent, cancellationToken);
        if (!createResp.IsSuccessStatusCode)
        {
            var errorBody = await createResp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("D21Panel settlement create failed. Status={StatusCode}, Body={Body}", (int)createResp.StatusCode, errorBody);
            return new(false, $"D21Panel settlement create failed ({(int)createResp.StatusCode}).");
        }

        var createDoc = await createResp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
        var settlementId = createDoc?.RootElement.TryGetProperty("settlementId", out var idEl) == true
            ? idEl.GetString()
            : null;
        var status = createDoc?.RootElement.TryGetProperty("status", out var statusEl) == true
            ? statusEl.GetString()
            : null;
        var paymentHash = createDoc?.RootElement.TryGetProperty("paymentHash", out var hashEl) == true
            ? hashEl.GetString()
            : null;

        if (string.Equals(status, "settled", StringComparison.OrdinalIgnoreCase))
            return new(true, null, paymentHash ?? settlementId);
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var err = createDoc?.RootElement.TryGetProperty("errorMessage", out var errEl) == true
                ? errEl.GetString()
                : "Settlement failed";
            return new(false, err ?? "Settlement failed");
        }

        if (string.IsNullOrWhiteSpace(settlementId))
            return new(false, "D21Panel settlement id missing.");

        for (var i = 0; i < _pollAttempts; i++)
        {
            await Task.Delay(_pollDelayMs, cancellationToken);
            using var pollResp = await client.GetAsync($"api/v1/cashumelt/settlements/{Uri.EscapeDataString(settlementId)}", cancellationToken);
            if (!pollResp.IsSuccessStatusCode)
                continue;

            var pollDoc = await pollResp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            var pollStatus = pollDoc?.RootElement.TryGetProperty("status", out var pollStatusEl) == true
                ? pollStatusEl.GetString()
                : null;
            var pollHash = pollDoc?.RootElement.TryGetProperty("paymentHash", out var pollHashEl) == true
                ? pollHashEl.GetString()
                : null;

            if (string.Equals(pollStatus, "settled", StringComparison.OrdinalIgnoreCase))
                return new(true, null, pollHash ?? settlementId);
            if (string.Equals(pollStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var err = pollDoc?.RootElement.TryGetProperty("errorMessage", out var pollErrEl) == true
                    ? pollErrEl.GetString()
                    : "Settlement failed";
                return new(false, err ?? "Settlement failed");
            }
        }

        return new(false, "D21Panel settlement did not reach terminal state in time.");
    }
}

