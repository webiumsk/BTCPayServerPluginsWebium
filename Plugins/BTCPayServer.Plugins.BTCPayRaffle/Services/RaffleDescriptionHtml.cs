#nullable enable
using System;
using BTCPayServer.Abstractions.Services;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleDescriptionHtml
{
    public const int MaximumLength = 8000;

    private readonly RichTextHtmlSanitizer _sanitizer;

    public RaffleDescriptionHtml(RichTextHtmlSanitizer sanitizer) => _sanitizer = sanitizer;

    public string? Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : _sanitizer.Sanitize(html.Trim());

    /// <summary>Validate the stored result because the host policy can expand markup.</summary>
    public string? SanitizeForStorage(string? description)
    {
        var sanitized = Sanitize(description);
        if (sanitized?.Length > MaximumLength)
            throw new ArgumentException($"Description must contain at most {MaximumLength} characters after sanitizing", nameof(description));
        return sanitized;
    }
}
