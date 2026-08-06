#nullable enable
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.LnAddress;

/// <summary>
/// Connection-string types this plugin claims and the curated wallet branding.
/// The plugin supersedes the Blitz and Flash plugins: their legacy types keep working
/// (including bare-username expansion to their historical default domains), while new
/// connections use <c>type=lnaddress</c> with a full user@domain address. Any domain whose
/// LNURL server supports LUD-21 verify works; the branding map is cosmetic only.
/// </summary>
public static class LnAddressTypes
{
    public const string Primary = "lnaddress";

    /// <summary>Legacy types from the superseded plugins → their bare-username default domain.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyDefaultDomains =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blitz"] = "blitzwalletapp.com",
            ["flash"] = "flashapp.me",
        };

    public static bool IsOurType(string? type) =>
        type is not null
        && (type.Equals(Primary, StringComparison.OrdinalIgnoreCase) || LegacyDefaultDomains.ContainsKey(type));

    /// <summary>Curated display names by LN address domain - cosmetic; unknown domains still work.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownWalletNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blitzwalletapp.com"] = "Blitz Wallet",
            ["flashapp.me"] = "Flash",
            ["coinos.io"] = "Coinos",
        };

    public static string DisplayNameFor(string domain) =>
        KnownWalletNames.TryGetValue(domain, out var name) ? name : $"LN Address ({domain})";
}
