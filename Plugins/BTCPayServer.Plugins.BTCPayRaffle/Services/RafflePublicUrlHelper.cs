#nullable enable
using System;
using System.Net;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RafflePublicUrlHelper
{
    public static bool TryGetTrustedOrigin(string? baseUrl, out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (string.IsNullOrEmpty(uri.Host))
            return false;
        origin = new Uri($"{uri.Scheme}://{uri.Authority}");
        return true;
    }

    public static string BuildPath(Uri origin, string pathAndQuery) =>
        new Uri(origin, pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery).ToString();

    public static string HtmlAttribute(string value) => WebUtility.HtmlEncode(value);
}
