using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

/// <summary>
/// The NOP integration manual's retry guidance: exponential backoff
/// 1 s, 2 s, 4 s ... capped at 30 s, at most 5 attempts.
/// </summary>
public static class NopBackoff
{
    public const int MaxAttempts = 5;

    /// <summary>Delay before retry attempt N (1-based). Attempts beyond the cap keep 30 s.</summary>
    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1)
            attempt = 1;
        // Clamp the exponent - C# shifts wrap modulo 32, so 1 << (attempt-1)
        // would cycle back to small delays for large attempt numbers.
        var exponent = Math.Min(attempt - 1, 5);
        var seconds = Math.Min(30, 1 << exponent);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The delays used for one bounded retry sequence: 1, 2, 4, 8 s (before attempts 2..5).</summary>
    public static IEnumerable<TimeSpan> RetryDelays()
    {
        for (var attempt = 1; attempt < MaxAttempts; attempt++)
            yield return DelayForAttempt(attempt);
    }
}
