#nullable enable
using System;

namespace BTCPayServer.Plugins.SatoshiTickets.Services;

internal static class CsvExportHelper
{
    public static string SanitizeForFormula(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@'))
            return "'" + trimmed;

        return trimmed;
    }

    public static string EscapeCsv(string? value)
    {
        var sanitized = SanitizeForFormula(value);
        return "\"" + sanitized.Replace("\"", "\"\"") + "\"";
    }
}
