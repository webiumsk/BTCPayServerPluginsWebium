#nullable enable

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RaffleBuyerDisplay
{
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "";

        email = email.Trim();
        var at = email.IndexOf('@');
        if (at <= 0 || at >= email.Length - 1)
            return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];
        var visible = local.Length <= 1 ? local : local[..1];

        return $"{visible}***@{domain}";
    }

    public static string DisplayBuyerName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "—" : name.Trim();
}
