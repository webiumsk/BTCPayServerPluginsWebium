#nullable enable
using System;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public static class CashuMeltSettingsValidation
{
    public static void ValidateOptionalFeeCaps(long? maxMeltFeeReserveSats, decimal? maxMeltFeeReservePercentOfMinted)
    {
        if (maxMeltFeeReserveSats is < 0)
            throw new InvalidOperationException("Max melt fee reserve (sats) cannot be negative.");

        if (maxMeltFeeReservePercentOfMinted is decimal p && (p < 0m || p > 100m))
            throw new InvalidOperationException("Max melt fee reserve percent must be between 0 and 100.");
    }
}
