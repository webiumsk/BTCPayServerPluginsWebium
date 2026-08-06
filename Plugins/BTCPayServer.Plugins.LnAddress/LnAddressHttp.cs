#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.LnAddress;

/// <summary>
/// Outbound-request safety for URLs that originate outside our control: LUD-21 verify URLs from
/// remote JSON, persisted tracked invoices, and store-configured domains. Two layers:
/// <see cref="IsSafeUrl"/> is the static accept-time policy (https only, default port, no
/// credentials, no IP literals), and <see cref="CreateSafeHandler"/> is the transport-level guard —
/// redirects disabled and every connect DNS-resolved and filtered against loopback/private/
/// link-local/reserved ranges, so a host that later re-resolves to an internal address (DNS
/// rebinding) still cannot be reached.
/// </summary>
internal static class LnAddressHttp
{
    /// <summary>Named HttpClient wired to <see cref="CreateSafeHandler"/> in <see cref="LnAddressPlugin"/>.</summary>
    public const string ClientName = "LnAddressSafeHttp";

    public static bool IsSafeUrl(Uri uri, out string? reason)
    {
        if (!uri.IsAbsoluteUri) { reason = "the URL must be absolute"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps) { reason = "only https URLs are allowed"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { reason = "URLs with embedded credentials are not allowed"; return false; }
        if (!uri.IsDefaultPort) { reason = "only the default https port (443) is allowed"; return false; }
        if (uri.HostNameType != UriHostNameType.Dns) { reason = "IP-literal hosts are not allowed"; return false; }
        reason = null;
        return true;
    }

    public static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                 // 0.0.0.0/8 "this network"
                || b[0] == 10                                // 10.0.0.0/8
                || b[0] == 127                               // loopback
                || (b[0] == 100 && (b[1] & 0xC0) == 64)      // 100.64.0.0/10 CGNAT
                || (b[0] == 169 && b[1] == 254)              // 169.254.0.0/16 link-local
                || (b[0] == 172 && (b[1] & 0xF0) == 16)      // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)              // 192.168.0.0/16
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)   // 192.0.0.0/24 IETF reserved
                || (b[0] == 198 && (b[1] & 0xFE) == 18)      // 198.18.0.0/15 benchmarking
                || b[0] >= 224;                              // multicast, reserved, broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal;
        }
        return true; // unknown address family: block
    }

    /// <summary>
    /// Handler for all plugin outbound requests: no redirect following (a redirect to an internal
    /// destination would bypass URL checks), and a connect callback that re-resolves DNS on every
    /// connection and refuses non-public addresses (defeats DNS rebinding).
    /// </summary>
    public static SocketsHttpHandler CreateSafeHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var safe = addresses.Where(a => !IsBlockedAddress(a)).ToArray();
            if (safe.Length == 0)
                throw new HttpRequestException(
                    $"Refusing to connect to '{ctx.DnsEndPoint.Host}': it does not resolve to a public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(safe, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
