#nullable enable
using System;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Public reveal delay so buyer wallet stays in sync with the presenter slot animation (~5.6s).
/// </summary>
public static class RaffleDrawReveal
{
    public static readonly TimeSpan RevealDelay = TimeSpan.FromSeconds(6);

    public static DateTimeOffset RevealAt(DateTimeOffset drawnAt) => drawnAt + RevealDelay;

    public static bool IsRevealed(DateTimeOffset drawnAt, DateTimeOffset? utcNow = null) =>
        (utcNow ?? DateTimeOffset.UtcNow) >= RevealAt(drawnAt);
}
