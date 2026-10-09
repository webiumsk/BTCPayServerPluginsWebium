#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.LnAddressConnect;

/// <summary>
/// Connection-string types this plugin claims and the curated wallet branding.
/// The plugin supersedes the address modes of the Blitz, Flash and Blink plugins: their legacy types keep working
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
            ["blink"] = "blink.sv",
        };

    /// <summary>Legacy type → assembly name of the superseded plugin that natively claims it.</summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyPluginAssemblies =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blitz"] = "BTCPayServer.Plugins.Blitz",
            ["flash"] = "BTCPayServer.Plugins.Flash",
            ["blink"] = "BTCPayServer.Plugins.Blink",
        };

    /// <summary>
    /// Loaded-assembly snapshot: plugin assemblies are all loaded before any connection
    /// string is handled, so caching once per process is safe.
    /// </summary>
    private static readonly Lazy<HashSet<string>> LoadedAssemblyNames = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this plugin should claim a legacy type. When the superseded plugin is
    /// still installed it keeps handling its own type - claiming it here too would make
    /// connection-string dispatch depend on DI registration order and run duplicate
    /// pollers/filters against the same wallet. The probe parameter is a test seam;
    /// production callers use the loaded-assembly snapshot.
    /// </summary>
    public static bool ClaimsLegacyType(string type, Func<string, bool>? legacyPluginLoadedProbe = null) =>
        LegacyPluginAssemblies.TryGetValue(type, out var assemblyName)
        && ! (legacyPluginLoadedProbe ?? LoadedAssemblyNames.Value.Contains)(assemblyName);

    public static bool IsOurType(string? type) =>
        type is not null
        && (type.Equals(Primary, StringComparison.OrdinalIgnoreCase)
            || (LegacyDefaultDomains.ContainsKey(type) && ClaimsLegacyType(type)));

    /// <summary>Claims only receive-only address configurations. Blink API keys and
    /// USD balances belong to the custodial plugin and must never be silently downgraded.</summary>
    public static bool IsOurConnection(string? type, IReadOnlyDictionary<string, string> parameters) =>
        IsOurType(type)
        && !parameters.ContainsKey("api-key") && !parameters.ContainsKey("apikey")
        && !parameters.ContainsKey("wallet-id") && !parameters.ContainsKey("walletid")
        && (!parameters.TryGetValue("currency", out var currency)
            || currency.Equals("BTC", StringComparison.OrdinalIgnoreCase));

    public static string? AddressParameter(string? type, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("ln-address", out var address)) return address;
        // Kukks Blink historically also accepts a username, defaulting to blink.sv.
        return type?.Equals("blink", StringComparison.OrdinalIgnoreCase) == true
            && (parameters.TryGetValue("username", out address) || parameters.TryGetValue("lnaddress", out address)) ? address : null;
    }

    /// <summary>Curated display names by LN address domain - cosmetic; unknown domains still work.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownWalletNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blitzwalletapp.com"] = "Blitz Wallet",
            ["blink.sv"] = "Blink",
            ["flashapp.me"] = "Flash",
            ["coinos.io"] = "Coinos",
        };

    public static string DisplayNameFor(string domain) =>
        KnownWalletNames.TryGetValue(domain, out var name) ? name : $"LN Address ({domain})";
}
