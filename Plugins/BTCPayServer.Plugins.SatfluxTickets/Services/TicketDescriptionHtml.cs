using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Preserves legacy contact text before the host rich-text sanitizer runs.</summary>
public static class TicketDescriptionHtml
{
    private static readonly Regex BracketedEmail = new(
        @"<[^<>\s]+@[^<>\s]+>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Markup = new(
        @"<[/!?]?[a-zA-Z][^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // The returned string is still untrusted HTML. Always pass it through
    // Safe.RawRichText rather than rendering it as raw output directly.
    public static string PreserveBracketedEmails(string value) =>
        BracketedEmail.Replace(value, match => HtmlEncoder.Default.Encode(match.Value));

    public static bool IsPlainText(string preparedValue) => !Markup.IsMatch(preparedValue);
}
