#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Embedded JSON strings for public raffle UI.
/// BTCPay sets <see cref="CultureInfo.CurrentUICulture"/> to Invariant on every request,
/// so language is resolved from <c>?lang=</c> or <c>Accept-Language</c> per HTTP request.
/// </summary>
public sealed class RaffleStringLocalizer
{
    public const string UiCultureItemKey = "RaffleUICulture";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "en", "sk", "es" };

    private readonly IHttpContextAccessor _httpContextAccessor;

    public RaffleStringLocalizer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string this[string key] => Get(key);

    public string Get(string key) => Get(key, ResolveRequestCulture());

    public string Get(string key, CultureInfo culture)
    {
        var lang = NormalizeLanguageCode(culture.TwoLetterISOLanguageName) ?? "en";
        var dict = Cache.GetOrAdd(lang, LoadLanguage);
        if (dict.TryGetValue(key, out var value))
            return value;
        if (!string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
        {
            var en = Cache.GetOrAdd("en", LoadLanguage);
            if (en.TryGetValue(key, out var enValue))
                return enValue;
        }
        return key;
    }

    public string Format(string key, params object[] args) => string.Format(Get(key), args);

    public IReadOnlyDictionary<string, string> ScriptPack(string prefix)
    {
        var lang = NormalizeLanguageCode(ResolveRequestCulture().TwoLetterISOLanguageName) ?? "en";
        var dict = Cache.GetOrAdd(lang, LoadLanguage);
        var en = Cache.GetOrAdd("en", LoadLanguage);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in dict)
        {
            if (kv.Key.StartsWith(prefix + ".", StringComparison.Ordinal))
                result[kv.Key[(prefix.Length + 1)..]] = kv.Value;
        }
        foreach (var kv in en)
        {
            if (!kv.Key.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
            var shortKey = kv.Key[(prefix.Length + 1)..];
            if (!result.ContainsKey(shortKey))
                result[shortKey] = kv.Value;
        }
        return result;
    }

    public CultureInfo ResolveRequestCulture()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
            return CultureInfo.GetCultureInfo("en");

        if (ctx.Request.Query.TryGetValue("lang", out var langQuery))
        {
            var fromQuery = NormalizeLanguageCode(langQuery.ToString());
            if (fromQuery is not null)
                return CultureInfo.GetCultureInfo(fromQuery);
        }

        if (ctx.Items.TryGetValue(UiCultureItemKey, out var storeLangObj)
            && storeLangObj is string storeLang)
        {
            var normalizedStoreLang = NormalizeLanguageCode(storeLang);
            if (normalizedStoreLang is not null)
                return CultureInfo.GetCultureInfo(normalizedStoreLang);
        }

        if (ctx.Request.Headers.TryGetValue("Accept-Language", out var acceptLanguage))
        {
            var fromHeader = PickLanguageFromAcceptHeader(acceptLanguage.ToString());
            if (fromHeader is not null)
                return CultureInfo.GetCultureInfo(fromHeader);
        }

        return CultureInfo.GetCultureInfo("en");
    }

    public static string? PickLanguageFromAcceptHeader(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return null;

        var candidates = acceptLanguage
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var segments = part.Split(';', 2, StringSplitOptions.TrimEntries);
                var code = segments[0];
                var q = 1.0;
                if (segments.Length == 2 && segments[1].StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(segments[1][2..], System.Globalization.NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed))
                {
                    q = parsed;
                }
                return (code, q);
            })
            .OrderByDescending(x => x.q);

        foreach (var (code, _) in candidates)
        {
            var normalized = NormalizeLanguageCode(code);
            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    public static string? NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var primary = code.Split('-')[0].Trim().ToLowerInvariant();
        return Supported.Contains(primary) ? primary : null;
    }

    private static IReadOnlyDictionary<string, string> LoadLanguage(string lang)
    {
        var assembly = typeof(RaffleStringLocalizer).Assembly;
        var resourceName = $"BTCPayServer.Plugins.BTCPayRaffle.Resources.Strings.{lang}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null && !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
            return LoadLanguage("en");
        if (stream is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var flat = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return flat ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
