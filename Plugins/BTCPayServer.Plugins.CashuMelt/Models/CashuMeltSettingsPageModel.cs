#nullable enable
using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;

namespace BTCPayServer.Plugins.CashuMelt.Models;

public class CashuMeltSettingsPageModel
{
    public required CashuMeltStoreSettings Settings { get; init; }
    public IReadOnlyList<CashuMeltRecentPaymentRow> RecentPayments { get; init; } = Array.Empty<CashuMeltRecentPaymentRow>();

    /// <summary>Echoed filter: settlement state (e.g. PENDING, MELT_COMPLETE) or empty.</summary>
    public string? FilterSettlement { get; init; }

    /// <summary>Echoed filter: substring match on BTCPay invoice id.</summary>
    public string? FilterInvoice { get; init; }

    /// <summary>Normalized mint base for NUT-23 poll URL hints.</summary>
    public string MintBaseNormalized { get; init; } = "";

    /// <summary>Accumulated NUT-08 change (sat) awaiting the background sweep to the Lightning address.</summary>
    public long PendingChangeSat { get; init; }

    /// <summary>Total NUT-08 change (sat) already swept to the merchant Lightning address.</summary>
    public long SweptChangeSat { get; init; }
}

public record CashuMeltRecentPaymentRow(
    string QuoteId,
    string InvoiceId,
    long AmountSats,
    string State,
    string SettlementState,
    string? SettlementError,
    DateTimeOffset CreatedAt,
    bool CanRetry,
    string? Bolt11Invoice,
    string MintQuotePollUrl,
    bool NeedsManualReview,
    int RetryCount,
    string? FailureReasonCode);
