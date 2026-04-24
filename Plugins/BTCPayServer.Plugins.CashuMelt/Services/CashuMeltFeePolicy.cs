#nullable enable
using System;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Optional merchant caps on melt Lightning fee reserve (routing reserve quoted by the mint).</summary>
public static class CashuMeltFeePolicy
{
    /// <returns>Error message or null if OK.</returns>
    public static string? ValidateMeltFeeReserve(
        long totalMintedSat,
        long feeReserve,
        long? maxFeeReserveSats,
        decimal? maxFeeReservePercentOfMinted)
    {
        if (feeReserve < 0)
            return "Mint returned a negative fee reserve.";

        if (maxFeeReserveSats.HasValue && feeReserve > maxFeeReserveSats.Value)
        {
            return
                $"Lightning routing fee reserve ({feeReserve} sat) exceeds the configured maximum ({maxFeeReserveSats.Value} sat). " +
                "Increase the payment amount, raise the limit in CashuMelt settings, or use a different Lightning address.";
        }

        if (maxFeeReservePercentOfMinted is decimal p)
        {
            if (p < 0m || p > 100m)
                return "Max fee percent must be between 0 and 100.";
            var cap = (long)Math.Ceiling((double)totalMintedSat * (double)p / 100.0);
            if (feeReserve > cap)
            {
                return
                    $"Lightning routing fee reserve ({feeReserve} sat) exceeds the configured maximum ({p}% of {totalMintedSat} sat = {cap} sat). " +
                    "Adjust CashuMelt fee limits or try a larger amount.";
            }
        }

        return null;
    }
}
