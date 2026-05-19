#nullable enable

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RaffleBuyerEmail
{
    public static string? Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        return email.Trim().ToLowerInvariant();
    }
}
