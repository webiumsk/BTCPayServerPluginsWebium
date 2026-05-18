using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// HTTP client for the CashuMelt mint API.
/// Implements NUT-04 (mint quotes), NUT-05 (minting), NUT-06 (keyset info), NUT-14 (melt).
/// https://github.com/cashubtc/nuts
/// </summary>
public class CashuMeltMintClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CashuMeltMintClient> _logger;

    // Snake_case for standard fields; explicit JsonPropertyName overrides for CashuMelt-specific names.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CashuMeltMintClient(HttpClient httpClient, ILogger<CashuMeltMintClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────
    // NUT-04 / NUT-23: Mint quotes (Lightning)
    // ──────────────────────────────────────────────────────────────

    /// <summary>POST /v1/mint/quote/bolt11 – request Lightning invoice from mint.</summary>
    public async Task<MintQuoteBolt11Response?> CreateMintQuoteAsync(
        string mintBaseUrl, long amount, string unit, CancellationToken ct = default)
    {
        var url = Url(mintBaseUrl, "/v1/mint/quote/bolt11");
        var requestBody = new { amount, unit };
        try
        {
            using var content = JsonContent.Create(requestBody, options: JsonOptions);
            var resp = await _httpClient.PostAsync(url, content, ct);
            var code = (int)resp.StatusCode;
            if (code is 429 or 502 or 503 or 504)
            {
                _logger.LogWarning("CreateMintQuote transient HTTP {Status} at {Url}", code, url);
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("CreateMintQuote HTTP {Status} at {Url}: {Body}", code, url,
                    errBody.Length > 400 ? errBody[..400] : errBody);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<MintQuoteBolt11Response>(stream, JsonOptions, ct);
            _logger.LogDebug("Mint quote created: {QuoteId} for {Amount} {Unit}", result?.Quote, amount, unit);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreateMintQuote failed at {Url}", url);
            return null;
        }
    }

    /// <summary>GET /v1/mint/quote/bolt11/{quoteId} – poll quote state.</summary>
    public async Task<MintQuotePollResult> GetMintQuoteForPollAsync(
        string mintBaseUrl, string quoteId, CancellationToken ct = default)
    {
        var url = $"{Url(mintBaseUrl)}/v1/mint/quote/bolt11/{Uri.EscapeDataString(quoteId)}";
        try
        {
            var resp = await _httpClient.GetAsync(url, ct);
            var code = (int)resp.StatusCode;

            if (code is 429 or 500 or 502 or 503 or 504)
            {
                var ra = ParseRetryAfterSeconds(resp);
                _logger.LogWarning(
                    "cashumelt_mint_poll_transient phase=mint_poll HTTP {Status} quote {QuoteId} retryAfter={RetryAfter}s",
                    code, quoteId, ra);
                return new MintQuotePollResult(false, true, ra, null, null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Mint quote poll HTTP {Status} for quote {QuoteId}: {Body}",
                    code, quoteId, body.Length > 500 ? body[..500] : body);
                return new MintQuotePollResult(false, false, null, $"Mint returned HTTP {code}", null);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var quote = await JsonSerializer.DeserializeAsync<MintQuoteBolt11Response>(stream, JsonOptions, ct);
            return new MintQuotePollResult(true, false, null, null, quote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HandleMintPollTransientFailure(mintBaseUrl, quoteId, ex);
        }
    }

    private MintQuotePollResult HandleMintPollTransientFailure(string mintBaseUrl, string quoteId, Exception ex)
    {
        const int retryAfter = 5;
        var reason = DescribePollFailure(ex);
        var mintHost = TryGetMintHost(mintBaseUrl);
        _logger.LogWarning(
            "{Tag} phase={Phase} mintHost={MintHost} quote={QuoteId} reason={Reason} retryAfter={RetryAfter}s",
            CashuMeltObservability.TagMintPollTransient,
            CashuMeltObservability.PhaseMintPoll,
            mintHost,
            quoteId,
            reason,
            retryAfter);
        return new MintQuotePollResult(false, true, retryAfter, null, null);
    }

    private static string TryGetMintHost(string mintBaseUrl)
    {
        if (Uri.TryCreate(mintBaseUrl, UriKind.Absolute, out var uri))
            return uri.Host;
        return mintBaseUrl;
    }

    private static string DescribePollFailure(Exception ex) => ex switch
    {
        TaskCanceledException => "request_timeout",
        HttpRequestException { InnerException: SocketException se } => $"socket_{se.SocketErrorCode}",
        HttpRequestException hre => string.IsNullOrWhiteSpace(hre.Message) ? "http_request_failed" : hre.Message,
        SocketException se => $"socket_{se.SocketErrorCode}",
        IOException => "io_error",
        JsonException => "invalid_mint_json",
        _ => ex.GetType().Name
    };

    private static int? ParseRetryAfterSeconds(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var first = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return null;
        if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec >= 0)
            return Math.Clamp(sec, 1, 300);
        if (DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until))
        {
            var s = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
            return Math.Clamp(s, 1, 300);
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────
    // NUT-06: Keyset info – get mint's public keys per denomination
    // ──────────────────────────────────────────────────────────────

    /// <summary>GET /v1/keys – active keyset public keys, indexed by denomination.</summary>
    public async Task<MintKeysResponse?> GetKeysAsync(
        string mintBaseUrl, CancellationToken ct = default)
    {
        var url = Url(mintBaseUrl, "/v1/keys");
        try
        {
            var resp = await _httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<MintKeysResponse>(stream, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetKeys failed at {Url}", url);
            throw;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // NUT-05: Minting tokens
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// POST /v1/mint/bolt11 – exchange paid mint quote for blind signatures.
    /// Outputs are blinded messages (B_ = Y + r·G); mint returns blind signatures (C_ = k·B_).
    /// </summary>
    public async Task<MintTokensResponse?> MintTokensAsync(
        string mintBaseUrl, string quoteId, BlindedMessage[] outputs, CancellationToken ct = default)
    {
        var url = Url(mintBaseUrl, "/v1/mint/bolt11");
        var body = new MintTokensRequest(quoteId, outputs);
        try
        {
            using var content = JsonContent.Create(body, options: JsonOptions);
            var resp = await _httpClient.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<MintTokensResponse>(stream, JsonOptions, ct);
            _logger.LogDebug("MintTokens: {Count} signatures for quote {QuoteId}", result?.Signatures?.Length, quoteId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MintTokens failed for quote {QuoteId}", quoteId);
            throw;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // NUT-14: Melt (pay Lightning invoice using CashuMelt proofs)
    // ──────────────────────────────────────────────────────────────

    /// <summary>POST /v1/melt/quote/bolt11 – get fee reserve estimate before melting.</summary>
    public async Task<MeltQuoteResponse?> RequestMeltQuoteAsync(
        string mintBaseUrl, string bolt11, string unit, CancellationToken ct = default)
    {
        var url = Url(mintBaseUrl, "/v1/melt/quote/bolt11");
        // CashuMelt spec uses "request" for the BOLT11 field in melt quotes
        var body = new { request = bolt11, unit };
        try
        {
            using var content = JsonContent.Create(body, options: JsonOptions);
            var resp = await _httpClient.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<MeltQuoteResponse>(stream, JsonOptions, ct);
            _logger.LogDebug("MeltQuote: {QuoteId} amount={Amount} feeReserve={Fee}", result?.Quote, result?.Amount, result?.FeeReserve);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RequestMeltQuote failed at {Url}", url);
            throw;
        }
    }

    /// <summary>
    /// POST /v1/melt/bolt11 – pay a Lightning invoice using CashuMelt proofs.
    /// On success, returns payment preimage and any change proofs.
    /// </summary>
    public async Task<MeltTokensResponse?> MeltTokensAsync(
        string mintBaseUrl, string meltQuoteId, CashuMeltProof[] inputs, CancellationToken ct = default)
    {
        var url = Url(mintBaseUrl, "/v1/melt/bolt11");
        var body = new MeltTokensRequest(meltQuoteId, inputs);
        try
        {
            using var content = JsonContent.Create(body, options: JsonOptions);
            var resp = await _httpClient.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("MeltTokens {Status} for melt quote {MeltQuoteId}: {Body}",
                    (int)resp.StatusCode, meltQuoteId, errorBody);
                resp.EnsureSuccessStatusCode(); // throw with status code
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<MeltTokensResponse>(stream, JsonOptions, ct);
            _logger.LogInformation("MeltTokens: paid={Paid} proof={Proof}", result?.Paid, result?.Proof);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MeltTokens failed for melt quote {MeltQuoteId}", meltQuoteId);
            throw;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static string Url(string baseUrl, string path = "") =>
        baseUrl.TrimEnd('/') + path;

    // ──────────────────────────────────────────────────────────────
    // Data transfer objects
    // ──────────────────────────────────────────────────────────────

    public record MintQuoteBolt11Response(
        string Quote,
        string? Request,
        long Amount,
        string Unit,
        string State,
        long? Expiry);

    /// <summary>
    /// Result of polling GET mint quote — avoids throwing on 429/502/503 so checkout poll stays 200 OK.
    /// </summary>
    public sealed record MintQuotePollResult(
        bool Success,
        bool TransientFailure,
        int? RetryAfterSeconds,
        string? ErrorMessage,
        MintQuoteBolt11Response? Quote);

    public record MintKeysResponse(MintKeyset[] Keysets);

    public record MintKeyset(
        string Id,
        string Unit,
        Dictionary<string, string> Keys);  // denomination (string) → compressed pubkey hex

    // Blinded message: B_ = Y + r·G  (sent to mint)
    public record BlindedMessage(
        [property: JsonPropertyName("amount")] long Amount,
        [property: JsonPropertyName("id")]     string Id,
        [property: JsonPropertyName("B_")]     string B_);

    // Blind signature: C_ = k·B_  (returned by mint)
    public record BlindSignature(
        [property: JsonPropertyName("amount")] long Amount,
        [property: JsonPropertyName("id")]     string Id,
        [property: JsonPropertyName("C_")]     string C_);

    public record MintTokensRequest(
        [property: JsonPropertyName("quote")]   string Quote,
        [property: JsonPropertyName("outputs")] BlindedMessage[] Outputs);

    public record MintTokensResponse(
        [property: JsonPropertyName("signatures")] BlindSignature[] Signatures);

    public record MeltQuoteResponse(
        string Quote,
        long Amount,
        long FeeReserve,
        string State,
        long? Expiry);

    public record MeltTokensRequest(
        [property: JsonPropertyName("quote")]  string Quote,
        [property: JsonPropertyName("inputs")] CashuMeltProof[] Inputs);

    public record MeltTokensResponse(
        bool Paid,
        string? Proof,       // Lightning payment preimage
        CashuMeltProof[]? Change); // leftover change proofs

    /// <summary>
    /// CashuMelt proof (unblinded token). C is the unblinded EC point.
    /// This type is used both as output of minting and as input to melt.
    /// </summary>
    public record CashuMeltProof(
        [property: JsonPropertyName("amount")] long Amount,
        [property: JsonPropertyName("id")]     string Id,
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("C")]      string C);
}
