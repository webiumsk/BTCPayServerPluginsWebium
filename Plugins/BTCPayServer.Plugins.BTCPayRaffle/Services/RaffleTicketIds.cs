#nullable enable
using System;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RaffleTicketIds
{
    public const string ManualPrefix = "manual:";

    public static string NewManual() => ManualPrefix + Guid.NewGuid();

    public static bool IsManual(string invoiceId) =>
        invoiceId.StartsWith(ManualPrefix, StringComparison.Ordinal);
}
