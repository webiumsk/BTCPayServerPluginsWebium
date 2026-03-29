#nullable enable
namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Result of <see cref="CashuMeltPaymentService.RetrySettlementAsync"/>.</summary>
public readonly record struct CashuMeltRetryOutcome(
    CashuMeltRetryKind Kind,
    bool Settled = false,
    string? Error = null,
    int? RetryAfterSeconds = null);

public enum CashuMeltRetryKind
{
    NotFound,
    AlreadySettled,
    CannotRetryMissingProofs,
    Completed
}
