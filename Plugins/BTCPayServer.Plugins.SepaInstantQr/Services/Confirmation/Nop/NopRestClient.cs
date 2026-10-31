#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    internal const int MaxNotificationPages = 100;
    internal const int MaxNotifications = 10_000;
    private const int MaxPageBytes = 1_048_576;
    private const int MaxFetchBytes = 20_971_520;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    internal NopRestClient(HttpClient httpClient, ILogger logger)
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
            AllowAutoRedirect = false,
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

    /// <summary>
    /// Retrieves the complete retained notification set. date_from filters ID
    /// creation, not bank notification receipt, so the initial request omits it.
    /// Only provider-issued, same-register pagination may add that parameter.
    /// Notifications expire two hours after bank delivery regardless of ID age.
    /// </summary>
    public async Task<JsonElement> GetAllTransactionsAsync(
        string pokladnicaId,
        CancellationToken cancellationToken)
    {
        var origin = _httpClient.BaseAddress;
        if (origin is null || origin.Scheme != Uri.UriSchemeHttps || origin.Port != 443
            || (origin.Host != new Uri(BaseUrlFor("PROD")).Host && origin.Host != new Uri(BaseUrlFor("INT")).Host))
            throw new NopRestException("NOP notification origin is not an approved provider host.");

        var path = $"/v1/getAllTransactions/{Uri.EscapeDataString(pokladnicaId)}";
        var alias = "/api" + path;
        var current = new Uri(origin, path);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var notifications = new List<JsonElement>();
        var totalBytes = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            for (var page = 0; page < MaxNotificationPages; page++)
            {
                if (!visited.Add(current.AbsoluteUri))
                    throw new NopRestException("NOP notification continuation is cyclic.");

                using var response = await _httpClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                await EnsureSuccess(response, "getAllTransactions", timeout.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new NopRestException("NOP returned an incomplete notification response.");

                var (rows, bytes) = await ReadNotificationPageAsync(response, MaxFetchBytes - totalBytes, timeout.Token);
                totalBytes += bytes;
                if (rows.ValueKind != JsonValueKind.Array)
                    throw new NopRestException("NOP returned an invalid notification collection.");
                if (notifications.Count + rows.GetArrayLength() > MaxNotifications)
                    throw new NopRestException("NOP notification count exceeds the bounded recovery limit.");

                var truncated = ReadHeader(response, "x-result-truncated", required: true);
                if (!bool.TryParse(truncated, out var hasMore))
                    throw new NopRestException("NOP notification truncation metadata is invalid.");
                ValidateCountHeader(response, "x-results-returned", rows.GetArrayLength(), requirePositive: false);
                ValidateCountHeader(response, "x-result-limit", rows.GetArrayLength(), requirePositive: true);
                foreach (var row in rows.EnumerateArray())
                    notifications.Add(row.Clone());

                var next = ReadNextLink(response);
                if (!hasMore)
                {
                    if (next is not null)
                        throw new NopRestException("NOP notification continuation conflicts with truncation metadata.");
                    return JsonSerializer.SerializeToElement(notifications);
                }
                if (rows.GetArrayLength() == 0 || next is null)
                    throw new NopRestException("NOP truncated notifications without a usable continuation.");

                current = ValidateContinuation(origin, path, alias, current, next);
            }
            throw new NopRestException("NOP notification pagination exceeds the bounded recovery limit.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NopRestException("NOP notification recovery timed out; retry the complete retained set.");
        }
        catch (JsonException)
        {
            throw new NopRestException("NOP returned malformed notification JSON.");
        }
        catch (HttpRequestException ex)
        {
            throw new NopRestException("NOP notification recovery transport failed.", ex.StatusCode);
        }
    }

    private static async Task<(JsonElement Rows, int Bytes)> ReadNotificationPageAsync(
        HttpResponseMessage response, int remainingBytes, CancellationToken ct)
    {
        var limit = Math.Min(MaxPageBytes, remainingBytes);
        if (limit <= 0 || response.Content.Headers.ContentLength > limit)
            throw new NopRestException("NOP notification response exceeds the bounded recovery size.");

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > limit)
                throw new NopRestException("NOP notification response exceeds the bounded recovery size.");
            output.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(output.ToArray());
        return (document.RootElement.Clone(), checked((int)output.Length));
    }

    private static string? ReadHeader(HttpResponseMessage response, string name, bool required)
    {
        var values = response.Headers.TryGetValues(name, out var header) ? header.ToArray() : [];
        if (values.Length > 1 || (required && values.Length != 1))
            throw new NopRestException("NOP notification pagination metadata is missing or ambiguous.");
        return values.Length == 0 ? null : values[0].Trim();
    }

    private static void ValidateCountHeader(HttpResponseMessage response, string name, int actual, bool requirePositive)
    {
        var value = ReadHeader(response, name, required: false);
        if (value is null)
            return;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < (requirePositive ? 1 : 0)
            || (requirePositive ? actual > parsed : actual != parsed))
            throw new NopRestException("NOP notification result count metadata is invalid.");
    }

    private static string? ReadNextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        string? next = null;
        foreach (var value in values)
        {
            foreach (var link in value.Split(','))
            {
                var part = link.Trim();
                var end = part.IndexOf('>');
                if (!part.StartsWith('<') || end <= 1)
                    throw new NopRestException("NOP notification continuation metadata is malformed.");
                var attributes = part[(end + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var relation = attributes.Where(a => a.StartsWith("rel=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (relation.Length != 1)
                    throw new NopRestException("NOP notification continuation relation is missing or ambiguous.");
                var relations = relation[0][4..].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!relations.Contains("next", StringComparer.OrdinalIgnoreCase))
                    continue;
                if (next is not null)
                    throw new NopRestException("NOP returned ambiguous next-page continuations.");
                next = part[1..end];
            }
        }
        return next;
    }

    private static Uri ValidateContinuation(Uri origin, string path, string alias, Uri current, string next)
    {
        if (!Uri.TryCreate(current, next, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || candidate.Port != origin.Port
            || !string.Equals(candidate.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0
            || (candidate.AbsolutePath != path && candidate.AbsolutePath != alias))
            throw new NopRestException("NOP notification continuation is outside the approved provider and cash register.");

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in candidate.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var name = WebUtility.UrlDecode(pair[0]);
            if (pair.Length != 2 || name is not ("date_from" or "after_id")
                || !parameters.TryAdd(name, WebUtility.UrlDecode(pair[1])))
                throw new NopRestException("NOP notification continuation parameters are invalid.");
        }
        if (!parameters.TryGetValue("date_from", out var date)
            || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            || !parameters.TryGetValue("after_id", out var id)
            || !PaymentReferenceGenerator.IsNopShaped(id))
            throw new NopRestException("NOP notification continuation cursor is missing or invalid.");

        // Canonicalize documented route aliases, parameter ordering and escaping
        // so cosmetic URL changes cannot evade cyclic-continuation detection.
        var canonicalDate = parsedDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        var canonicalId = "QR-" + id[3..].ToLowerInvariant();
        return new Uri(origin, path + "?date_from=" + Uri.EscapeDataString(canonicalDate)
            + "&after_id=" + Uri.EscapeDataString(canonicalId));
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static Task EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return Task.CompletedTask;

        // Provider bodies can contain bank/account evidence; do not copy them
        // into exceptions that are subsequently logged or returned to the UI.
        throw new NopRestException($"NOP {operation} failed with HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    public void Dispose() => _httpClient.Dispose();
}
