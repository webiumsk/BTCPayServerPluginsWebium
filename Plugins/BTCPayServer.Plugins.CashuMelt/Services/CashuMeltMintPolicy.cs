#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Normalization and allow-list checks for mint URLs. We never custodian customer ecash;
/// this only constrains which mint HTTPS origins the store may call.
/// </summary>
public static class CashuMeltMintPolicy
{
    public static string NormalizeMintUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        var u = url.Trim().TrimEnd('/');
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            var noSlash = uri.ToString().TrimEnd('/');
            return noSlash.Length > 0 ? noSlash : u;
        }
        return u;
    }

    /// <summary>Split trusted list from textarea: newlines or commas.</summary>
    public static IReadOnlyList<string> ParseTrustedMintLines(string? trustedMintUrls)
    {
        if (string.IsNullOrWhiteSpace(trustedMintUrls))
            return Array.Empty<string>();

        return trustedMintUrls
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(NormalizeMintUrl)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// When <see cref="CashuMeltStoreSettings.TrustedMintUrls"/> is empty, the configured <see cref="CashuMeltStoreSettings.MintUrl"/> alone is allowed.
    /// When non-empty, <see cref="CashuMeltStoreSettings.MintUrl"/> must match one of the listed origins (after normalization).
    /// </summary>
    public static void ValidateStoreMintAgainstTrustedList(CashuMeltStoreSettings settings)
    {
        var mint = NormalizeMintUrl(settings.MintUrl);
        if (string.IsNullOrEmpty(mint))
            throw new InvalidOperationException("Mint URL is required.");

        if (!mint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mint URL must use HTTPS.");

        var lines = ParseTrustedMintLines(settings.TrustedMintUrls);
        if (lines.Count == 0)
            return;

        foreach (var line in lines)
        {
            if (!line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Trusted mint URL must use HTTPS: {line}");
        }

        if (!lines.Any(l => string.Equals(l, mint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Configured Cashu Mint URL is not in the trusted mint list. Either add it to the list or clear the list to only use the primary mint URL.");
        }
    }

    /// <summary>Verify an outbound mint base URL matches the store's effective trusted set.</summary>
    public static bool IsMintAllowed(CashuMeltStoreSettings settings, string mintBaseUrlUsed)
    {
        var used = NormalizeMintUrl(mintBaseUrlUsed);
        var primary = NormalizeMintUrl(settings.MintUrl);
        var lines = ParseTrustedMintLines(settings.TrustedMintUrls);
        var allowed = lines.Count > 0
            ? lines
            : new[] { primary };

        return allowed.Any(a => string.Equals(a, used, StringComparison.OrdinalIgnoreCase));
    }
}
