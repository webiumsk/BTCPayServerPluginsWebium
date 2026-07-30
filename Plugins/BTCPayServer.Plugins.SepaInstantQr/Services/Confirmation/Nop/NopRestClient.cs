#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

public class NopRestException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public NopRestException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// NOP Lite ERP REST API over mTLS (see docs/research/nop.md). One instance
/// per call site; the HttpClient carries the store's eKasa certificate, so
/// clients are created per store via <see cref="Create"/> and disposed.
/// </summary>
public sealed class NopRestClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    private NopRestClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public static string BaseUrlFor(string environment)
        => environment?.ToUpperInvariant() == "PROD"
            ? "https://api-erp.kverkom.sk"
            : "https://api-erp-i.kverkom.sk";

    public static string MqttHostFor(string environment)
        => environment?.ToUpperInvariant() == "PROD" ? "mqtt.kverkom.sk" : "mqtt-i.kverkom.sk";

    public static NopRestClient Create(SepaBackendCredentials credentials, ILogger logger)
    {
        var certificate = NopCertificateLoader.Load(credentials);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { certificate },
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(BaseUrlFor(credentials.NopEnvironment)),
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new NopRestClient(httpClient, logger);
    }

    public async Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/v1/status", cancellationToken);
        await EnsureSuccess(response, "status", cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// POST /v1/generateNewTransactionId with the manual's retry guidance
    /// (backoff 1,2,4,8 s; max 5 attempts) on 429/5xx. The response field is
    /// `id` per the Services API doc but `transaction_id` in the integration
    /// manual example - both are accepted.
    /// </summary>
    public async Task<string> GenerateNewTransactionIdAsync(string? comment, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                // Explicit JsonContent - BTCPayServer ships its own
                // PostAsJsonAsync extension that collides with System.Net.Http.Json.
                using var content = JsonContent.Create(
                    string.IsNullOrEmpty(comment) ? new { } : (object)new { comment });
                using var response = await _httpClient.PostAsync(
                    "/v1/generateNewTransactionId", content, cancellationToken);

                if (IsRetryable(response.StatusCode) && attempt < NopBackoff.MaxAttempts)
                {
                    await Task.Delay(NopBackoff.DelayForAttempt(attempt), cancellationToken);
                    continue;
                }

                await EnsureSuccess(response, "generateNewTransactionId", cancellationToken);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                var id = ReadTransactionId(json);
                if (string.IsNullOrEmpty(id))
                    throw new NopRestException("generateNewTransactionId response carried no transaction id.");
                return id;
            }
            catch (HttpRequestException ex) when (attempt < NopBackoff.MaxAttempts)
            {
                _logger.LogWarning(ex, "NOP generateNewTransactionId attempt {Attempt} failed; retrying", attempt);
                await Task.Delay(NopBackoff.DelayForAttempt(attempt), cancellationToken);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested && attempt < NopBackoff.MaxAttempts)
            {
                // HttpClient timeouts surface as TaskCanceledException while
                // the caller's token is still live - retry those; genuine
                // caller cancellations rethrow via the `when` filter.
                _logger.LogWarning(ex, "NOP generateNewTransactionId attempt {Attempt} timed out; retrying", attempt);
                await Task.Delay(NopBackoff.DelayForAttempt(attempt), cancellationToken);
            }
        }
    }

    internal static string? ReadTransactionId(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return null;
        if (json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (json.TryGetProperty("transaction_id", out var txId) && txId.ValueKind == JsonValueKind.String)
            return txId.GetString();
        return null;
    }

    /// <summary>GET /v1/getAllTransactions/{POKLADNICA-...}?date_from=... - notifications expire after 2 h.</summary>
    public async Task<JsonElement> GetAllTransactionsAsync(
        string pokladnicaId,
        DateTimeOffset? dateFrom,
        CancellationToken cancellationToken)
    {
        var path = $"/v1/getAllTransactions/{Uri.EscapeDataString(pokladnicaId)}";
        if (dateFrom is not null)
            path += $"?date_from={Uri.EscapeDataString(dateFrom.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))}";

        using var response = await _httpClient.GetAsync(path, cancellationToken);
        await EnsureSuccess(response, "getAllTransactions", cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static async Task EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        // Bodies may carry error details but never secrets; cap the length.
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 300)
            body = body[..300];
        throw new NopRestException($"NOP {operation} failed with HTTP {(int)response.StatusCode}: {body}", response.StatusCode);
    }

    public void Dispose() => _httpClient.Dispose();
}
