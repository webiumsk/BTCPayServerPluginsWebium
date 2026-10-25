#nullable enable
using System;
using System.Linq;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Formats existing public image URLs for server-rendered share metadata; never fetches resources.</summary>
public static class TicketSalesSocialMetadata
{
    public static string? AbsoluteHttpUrl(Uri root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return null;
        // Host roots include PathBase without a trailing slash: treat that path as a directory.
        var directory = new Uri(root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        if (!Uri.TryCreate(directory, value, out var url) || !url.IsAbsoluteUri
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(url.UserInfo)) return null;
        return url.AbsoluteUri;
    }

    public static string? ImageUrl(Uri root, string? eventImage, string? storeLogo) =>
        AbsoluteHttpUrl(root, string.IsNullOrWhiteSpace(eventImage) ? storeLogo : eventImage);
}
