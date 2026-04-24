#nullable enable
using System;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public static class CashuMeltNutsUrls
{
    /// <summary>NUT-23 poll path for a mint Lightning quote id (for operator debugging).</summary>
    public static string MintQuoteBolt11PollUrl(string mintBaseUrl, string quoteId)
    {
        var b = CashuMeltMintPolicy.NormalizeMintUrl(mintBaseUrl);
        return $"{b}/v1/mint/quote/bolt11/{Uri.EscapeDataString(quoteId)}";
    }
}
