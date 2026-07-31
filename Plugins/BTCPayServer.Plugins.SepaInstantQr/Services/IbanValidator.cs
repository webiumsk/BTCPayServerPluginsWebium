using System.Collections.Generic;
using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// IBAN normalization + ISO 13616 mod-97 checksum validation. Chunked
/// remainder computation - no BigInteger needed.
/// </summary>
public static class IbanValidator
{
    // Exact lengths for the markets the plugin targets; everything else is
    // validated against the generic 15-34 window plus the checksum.
    private static readonly Dictionary<string, int> KnownLengths = new()
    {
        ["SK"] = 24, ["CZ"] = 24, ["AT"] = 20, ["DE"] = 22, ["HU"] = 28,
        ["PL"] = 28, ["SI"] = 19, ["HR"] = 21, ["NL"] = 18, ["BE"] = 16,
        ["FR"] = 27, ["ES"] = 24, ["IT"] = 27, ["PT"] = 25, ["IE"] = 22,
        ["FI"] = 18, ["LU"] = 20, ["LT"] = 20, ["LV"] = 21, ["EE"] = 20,
    };

    /// <summary>Uppercase, whitespace stripped.</summary>
    public static string Normalize(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
            return string.Empty;

        var sb = new StringBuilder(iban.Length);
        foreach (var c in iban)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    public static bool IsValid(string? iban)
    {
        var normalized = Normalize(iban);
        if (normalized.Length is < 15 or > 34)
            return false;

        // [A-Z]{2}[0-9]{2}[A-Za-z0-9]{1,30} (already uppercased)
        if (!char.IsAsciiLetterUpper(normalized[0]) || !char.IsAsciiLetterUpper(normalized[1]))
            return false;
        if (!char.IsAsciiDigit(normalized[2]) || !char.IsAsciiDigit(normalized[3]))
            return false;
        for (var i = 4; i < normalized.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(normalized[i]))
                return false;
        }

        var country = normalized[..2];
        if (KnownLengths.TryGetValue(country, out var expected) && normalized.Length != expected)
            return false;

        return Mod97(normalized) == 1;
    }

    private static int Mod97(string normalized)
    {
        // Rearrange: move the first four characters to the end, then map
        // letters to numbers (A=10..Z=35) and reduce mod 97 incrementally.
        var rearranged = normalized[4..] + normalized[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            if (char.IsAsciiDigit(c))
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else
            {
                var value = c - 'A' + 10;
                remainder = (remainder * 100 + value) % 97;
            }
        }

        return remainder;
    }
}
