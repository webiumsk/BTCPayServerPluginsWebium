#nullable enable
using BTCPayServer.Abstractions.Services;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleDescriptionHtml
{
    private readonly RichTextHtmlSanitizer _sanitizer;

    public RaffleDescriptionHtml(RichTextHtmlSanitizer sanitizer) => _sanitizer = sanitizer;

    public string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : _sanitizer.Sanitize(html.Trim());
}
