using System.Globalization;
using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>Shared text normalization for QR payloads.</summary>
public static class QrText
{
    /// <summary>
    /// Replaces diacritics with ASCII equivalents (recommended by the SBA
    /// Payment Link Standard Annex A and required for compact SPD codes),
    /// then drops any remaining non-ASCII characters.
    /// </summary>
    public static string ToAscii(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (c <= 127)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>EUR amount: dot separator, exactly two decimals, no grouping.</summary>
    public static string Amount(decimal amount)
        => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
