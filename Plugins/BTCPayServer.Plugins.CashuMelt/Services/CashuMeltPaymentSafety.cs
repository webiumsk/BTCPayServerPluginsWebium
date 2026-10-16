#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>The wallet-change predicate and metadata are classifications, never recovery authorization.</summary>
public static class CashuMeltPaymentSafety
{
    public static readonly Expression<Func<CashuMeltPaymentRequest, bool>> Unresolved = r =>
        (r.SettlementState == null || (r.SettlementState != "SETTLED" && r.SettlementState != "EXPIRED_UNPAID"))
        || r.MintedProofsJson != null || r.MintIssuanceJson != null || r.BlankOutputsJson != null
        || (r.MeltQuoteId != null && r.SettlementState != "SETTLED");

    // Queries project booleans, not the private columns themselves.
    public sealed record Facts(string? State, bool HasOriginalSettings, bool HasProofs,
        bool HasIssuance, bool HasChangeOutputs, bool HasPayoutQuote);
    public sealed record Diagnostic(bool BlocksWalletChange, string[] WalletChangeBlockReasons, string RecoveryPhase);

    public static Diagnostic Describe(Facts facts)
    {
        var reasons = new List<string>();
        var terminal = facts.State is "SETTLED" or "EXPIRED_UNPAID";
        if (!terminal) reasons.Add("non_terminal_settlement");
        if (facts.HasProofs) reasons.Add("retained_minted_proofs");
        if (facts.HasIssuance) reasons.Add("retained_issuance_intent");
        if (facts.HasChangeOutputs) reasons.Add("retained_change_outputs");
        if (facts.HasPayoutQuote && facts.State != "SETTLED") reasons.Add("retained_payout_quote");
        var blocked = reasons.Count > 0;
        var phase = !blocked ? (facts.State == "SETTLED" ? "complete" : "expired_unpaid")
            : terminal ? "terminal_recovery_review"
            : !facts.HasOriginalSettings ? "original_settings_required"
            : facts.State == "MELT_COMPLETE" ? "accounting_pending"
            : facts.HasPayoutQuote ? "payout_reconciliation"
            : facts.HasProofs ? "proofs_retained"
            : facts.HasIssuance ? "issuance_restore"
            : facts.HasChangeOutputs ? "recovery_evidence_review"
            : "funding_verification";
        return new Diagnostic(blocked, reasons.ToArray(), phase);
    }
}
