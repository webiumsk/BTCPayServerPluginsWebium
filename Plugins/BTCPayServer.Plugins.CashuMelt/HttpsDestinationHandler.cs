#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.CashuMelt;

/// <summary>Scheme/credential validation before the host's DNS-pinned SSRF transport.
/// HTTP services need an exact operator origin and private services also need SSRFEXCEPTIONS.</summary>
public sealed class HttpsDestinationHandler : DelegatingHandler
{
    public static bool IsAllowed(Uri? uri, string? allowedHttpOrigins)
    {
        if (uri is null || !uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Scheme == "https") return true;
        if (uri.Scheme != "http") return false;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return (allowedHttpOrigins ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => Uri.TryCreate(value, UriKind.Absolute, out var allowed) &&
                allowed.Scheme == "http" && string.IsNullOrEmpty(allowed.UserInfo) &&
                allowed.AbsolutePath == "/" && string.IsNullOrEmpty(allowed.Query) && string.IsNullOrEmpty(allowed.Fragment) &&
                allowed.GetLeftPart(UriPartial.Authority).Equals(origin, StringComparison.OrdinalIgnoreCase));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsAllowed(request.RequestUri, Environment.GetEnvironmentVariable("BTCPAY_PLUGIN_HTTP_ALLOWED_HTTP_ORIGINS")))
            throw new HttpRequestException("Plugin destination requires HTTPS without credentials or an explicitly authorized HTTP origin");
        return base.SendAsync(request, cancellationToken);
    }
}
