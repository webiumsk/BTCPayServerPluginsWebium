#nullable enable
using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;

namespace BTCPayServer.Plugins.CashuMelt.Models;

public class CashuMeltSettingsPageModel
{
    public required CashuMeltStoreSettings Settings { get; init; }
    public IReadOnlyList<CashuMeltRecentPaymentRow> RecentPayments { get; init; } = Array.Empty<CashuMeltRecentPaymentRow>();
}

public record CashuMeltRecentPaymentRow(
    string QuoteId,
    string InvoiceId,
    long AmountSats,
    string State,
    string SettlementState,
    string? SettlementError,
    DateTimeOffset CreatedAt,
    bool CanRetry);
