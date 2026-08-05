#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Blitz;

/// <summary>The outcome of resolving a Blitz Lightning address: its LNURL-pay metadata endpoint
/// (the connection's identity) and the host shown in the UI.</summary>
public sealed record ResolvedBlitz(Uri PayEndpoint, string LnAddress, string DisplayHost);

public static class BlitzResolver
{
    public const string DefaultDomain = "blitzwalletapp.com";

    /// <summary>Expands a bare username to user@blitzwalletapp.com; full addresses pass through.</summary>
    public static string NormalizeAddress(string lnAddress)
    {
        lnAddress = lnAddress.Trim();
        return lnAddress.Contains('@') ? lnAddress : $"{lnAddress}@{DefaultDomain}";
    }

    public static (string Username, string Domain) ParseLightningAddress(string lnAddress)
    {
        var parts = NormalizeAddress(lnAddress).Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new FormatException($"'{lnAddress}' is not a valid Lightning address.");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Resolves a Blitz Lightning address to its LNURL-pay endpoint and validates it by fetching the
    /// metadata (an address whose server is unreachable or errors fails here, at config time).
    /// </summary>
    public static async Task<ResolvedBlitz> Resolve(string lnAddress, HttpClient http, CancellationToken ct)
    {
        lnAddress = NormalizeAddress(lnAddress);
        var endpoint = LNURL.LNURL.ExtractUriFromInternetIdentifier(lnAddress);

        var root = await GetJson(http, endpoint, ct);
        var tag = root["tag"]?.Value<string>();
        if (tag != "payRequest")
            throw new FormatException($"'{lnAddress}' did not resolve to an LNURL-pay endpoint (tag '{tag}').");

        return new ResolvedBlitz(endpoint, lnAddress, endpoint.Host);
    }

    internal static async Task<JObject> GetJson(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var resp = await http.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new FormatException($"LNURL endpoint '{uri}' returned HTTP {(int)resp.StatusCode}.");
        var json = JObject.Parse(body);
        if (json["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            throw new FormatException(json["reason"]?.Value<string>() ?? $"LNURL endpoint '{uri}' returned an error.");
        return json;
    }
}
