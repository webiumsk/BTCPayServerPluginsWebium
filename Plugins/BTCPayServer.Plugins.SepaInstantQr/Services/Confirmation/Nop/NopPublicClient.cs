#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

public enum NopPublicLookupStatus
{
    /// <summary>NOP knows the id - see <see cref="NopPublicLookupResult.History"/>.</summary>
    Found,
    /// <summary>NOP has never seen the id (locally generated references, or no bank notification yet).</summary>
    NotFound,
    /// <summary>Not a NOP-shaped id (QR- + 32 hex) - nothing was sent.</summary>
    InvalidId,
    /// <summary>The service could not answer (rate limit, WAF block, outage, timeout).</summary>
    Unavailable,
}

/// <summary>
/// Diagnostic timeline of one NOP transaction id as the public "Kde je moja
/// platba" API reports it. Every field is optional - the service only fills
/// what has happened so far. No creditor account is exposed, so this is a
/// hint about the payment's journey, never proof that the merchant's own
/// account was credited.
/// </summary>
public sealed class NopTransactionHistory
{
    public string? TransactionId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
    public DateTimeOffset? MatchedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? OrganizationName { get; init; }
    public string? OrganizationId { get; init; }
    public string? Status { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public string RawJson { get; init; } = string.Empty;
}

public sealed record NopPublicLookupResult(
    NopPublicLookupStatus Status,
    NopTransactionHistory? History,
    string? Message,
    int? HttpStatus)
{
    public static NopPublicLookupResult Invalid()
        => new(NopPublicLookupStatus.InvalidId, null, "Not a NOP transaction id (QR- followed by 32 hex characters).", null);
}

/// <summary>
/// Public NOP diagnostics API ("Kde je moja platba", Finančná správa SR):
/// GET {host}/api/v1/getTransactionHistory/{transactionId} - no mTLS, no API
/// key, RFC 7807 errors (404 "Transaction not found", 400 "Invalid URL
/// parameter"). https://www.kdejemojaplatba.sk is a third-party front-end
/// over the same endpoint with its own IP rate limit - this client talks to
/// the official host directly. Never throws: every failure maps to
/// <see cref="NopPublicLookupStatus.Unavailable"/> so callers can show it.
/// </summary>
public class NopPublicClient
{
    public const string ProdBaseUrl = "https://kdejemojaplatba.kverkom.sk";
    public const string IntBaseUrl = "https://kdejemojaplatba-i.kverkom.sk";

    /// <summary>Per-call cap - the settings page and the API wait on this synchronously.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly string UserAgent =
        "BTCPayServer.Plugins.SepaInstantQr/" + (typeof(NopPublicClient).Assembly.GetName().Version?.ToString(3) ?? "0");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NopPublicClient> _logger;

    public NopPublicClient(IHttpClientFactory httpClientFactory, ILogger<NopPublicClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static string BaseUrlFor(string? environment)
        => string.Equals(environment, "PROD", StringComparison.OrdinalIgnoreCase) ? ProdBaseUrl : IntBaseUrl;

    public static string HistoryUrlFor(string? environment, string transactionId)
        => $"{BaseUrlFor(environment)}/api/v1/getTransactionHistory/{Uri.EscapeDataString(transactionId)}";

    public async Task<NopPublicLookupResult> GetTransactionHistoryAsync(
        string? environment, string transactionId, CancellationToken cancellationToken)
    {
        transactionId = transactionId?.Trim() ?? string.Empty;
        if (!PaymentReferenceGenerator.IsNopShaped(transactionId))
            return NopPublicLookupResult.Invalid();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(NopPublicClient));
            using var request = new HttpRequestMessage(HttpMethod.Get, HistoryUrlFor(environment, transactionId));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var status = (int)response.StatusCode;

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var history = Parse(body);
                    if (history is null)
                    {
                        _logger.LogWarning("NOP public lookup {Id} returned HTTP {Status} with an unexpected payload", transactionId, status);
                        return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, "NOP answered with an unexpected payload.", status);
                    }
                    return new NopPublicLookupResult(NopPublicLookupStatus.Found, history, null, status);
                case HttpStatusCode.NotFound:
                    return new NopPublicLookupResult(NopPublicLookupStatus.NotFound, null, ProblemDetail(body) ?? "Transaction not found", status);
                case HttpStatusCode.BadRequest:
                    return new NopPublicLookupResult(NopPublicLookupStatus.InvalidId, null, ProblemDetail(body) ?? "Invalid transaction id", status);
                case HttpStatusCode.TooManyRequests:
                    return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, "NOP rate limit reached - try again in a minute.", status);
                case HttpStatusCode.Forbidden:
                    return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, "NOP refused the request (403) - the server's IP range may be blocked.", status);
                default:
                    _logger.LogWarning("NOP public lookup {Id} returned HTTP {Status}", transactionId, status);
                    return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, $"NOP answered HTTP {status}.", status);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, $"NOP did not answer within {Timeout.TotalSeconds:0} seconds.", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "NOP public lookup {Id} failed", transactionId);
            return new NopPublicLookupResult(NopPublicLookupStatus.Unavailable, null, "NOP is not reachable right now.", null);
        }
    }

    /// <summary>
    /// Lookup for a store's payment request. Ids of a store holding a NOP
    /// certificate were issued in its current environment - but a store
    /// moved from INT to PROD still has older INT-issued ids, so a
    /// "not found" there is retried in the other environment. Stores
    /// without a certificate only ever have bank-reported ids, which exist
    /// in PROD alone. Returns the environment that answered.
    /// </summary>
    public async Task<(string Environment, NopPublicLookupResult Result)> LookupForStoreAsync(
        SepaBackendCredentials? credentials, string reference, CancellationToken cancellationToken)
    {
        var hasCertificate = credentials?.HasNopCertificate == true;
        var primary = hasCertificate ? NormalizeEnvironment(credentials!.NopEnvironment) : "PROD";
        var result = await GetTransactionHistoryAsync(primary, reference, cancellationToken);
        if (!hasCertificate || result.Status != NopPublicLookupStatus.NotFound)
            return (primary, result);

        var other = primary == "PROD" ? "INT" : "PROD";
        var fallback = await GetTransactionHistoryAsync(other, reference, cancellationToken);
        return fallback.Status == NopPublicLookupStatus.Found ? (other, fallback) : (primary, result);
    }

    public static string NormalizeEnvironment(string? environment)
        => BaseUrlFor(environment) == ProdBaseUrl ? "PROD" : "INT";

    /// <summary>
    /// Tolerant mapping of the history payload: the field names were taken
    /// from the public front-end (camelCase) and the ERP API (snake_case
    /// `created_at`, `happened_at`), so both spellings are accepted and
    /// anything unknown is kept in <see cref="NopTransactionHistory.RawJson"/>.
    /// </summary>
    internal static NopTransactionHistory? Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        decimal? amount = null;
        string? currency = null;
        var payment = FirstObject(root, "payment", "transactionAmount", "transaction_amount");
        if (payment is { } p)
        {
            amount = ReadDecimal(p, "amount");
            currency = ReadString(p, "currency");
        }
        amount ??= ReadDecimal(root, "amount");
        currency ??= ReadString(root, "currency");

        return new NopTransactionHistory
        {
            TransactionId = ReadString(root, "transactionId", "transaction_id", "id"),
            CreatedAt = ReadDate(root, "createdAt", "created_at"),
            IndexedAt = ReadDate(root, "indexedAt", "indexed_at"),
            MatchedAt = ReadDate(root, "matchedAt", "matched_at"),
            PublishedAt = ReadDate(root, "publishedAt", "published_at"),
            ReceivedAt = ReadDate(root, "receivedAt", "received_at"),
            OrganizationName = ReadString(root, "organizationName", "organization_name"),
            OrganizationId = ReadString(root, "organizationId", "organization_id"),
            Status = ReadString(root, "status", "transactionStatus", "transaction_status"),
            Amount = amount,
            Currency = currency?.ToUpperInvariant(),
            RawJson = body,
        };
    }

    private static string? ProblemDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadString(document.RootElement, "detail", "title")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? FirstObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }
        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                    break;
                case JsonValueKind.Number:
                    return value.GetRawText();
            }
        }
        return null;
    }

    private static decimal? ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        var text = ReadString(element, names);
        return text is not null
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }
}
