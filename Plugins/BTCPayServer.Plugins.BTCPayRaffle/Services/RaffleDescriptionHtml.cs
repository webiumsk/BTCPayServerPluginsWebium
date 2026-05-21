#nullable enable
using Ganss.Xss;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleDescriptionHtml
{
    private readonly HtmlSanitizer _sanitizer;

    public RaffleDescriptionHtml(HtmlSanitizer sanitizer) => _sanitizer = sanitizer;

    public string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : _sanitizer.Sanitize(html.Trim());
}
