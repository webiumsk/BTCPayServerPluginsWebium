using System;

namespace BTCPayServer.Plugins.CashuMelt.Errors;

/// <summary>A temporary funding verification failure; preserves the original recovery data.</summary>
public sealed class CashuMeltRecoveryUnavailableException(int? retryAfterSeconds)
    : InvalidOperationException("Original mint verification is temporarily unavailable; wait and try again. Original settings were not confirmed.")
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
