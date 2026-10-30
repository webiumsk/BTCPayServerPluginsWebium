using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Matches the invoice redirect contract without rewriting merchant values.</summary>
public sealed class TicketRedirectUrlAttribute : ValidationAttribute
{
    public const string ValidationMessage = "Redirect URL must be an absolute HTTP or HTTPS URL of at most 2048 characters";

    public TicketRedirectUrlAttribute() => ErrorMessage = ValidationMessage;

    public static bool IsAllowed(string value) => string.IsNullOrEmpty(value)
        || value.Length <= LengthLimits.MaxUrlLength && value == value.Trim()
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    public override bool IsValid(object value) => value is null || value is string url && IsAllowed(url);
}
