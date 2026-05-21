#nullable enable
using System;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RaffleTicketIds
{
    public const string ManualPrefix = "manual:";
    public const string EventBundlePrefix = "eventbundle:";

    public static string NewManual() => ManualPrefix + Guid.NewGuid();

    public static string EventBundle(string eventOrderId, string normalizedEmail) =>
        $"{EventBundlePrefix}{eventOrderId}:{normalizedEmail}";

    public static bool IsManual(string invoiceId) =>
        invoiceId.StartsWith(ManualPrefix, StringComparison.Ordinal)
        || invoiceId.StartsWith(EventBundlePrefix, StringComparison.Ordinal);

    public static bool IsEventBundle(string invoiceId) =>
        invoiceId.StartsWith(EventBundlePrefix, StringComparison.Ordinal);
}
