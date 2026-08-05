#nullable enable
using System;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Optional merchant caps on melt Lightning fee reserve (routing reserve quoted by the mint).</summary>
public static class CashuMeltFeePolicy
{
    /// <summary>
    /// Estimated Lightning routing fee buffer withheld from the minted amount before
    /// resolving the merchant invoice. Mirrors the common mint default reserve of
    /// max(2 sat, 1% of amount); deliberately uncapped - a hard cap made every payment
    /// above cap/1% fail against mints with a percentage-based reserve.
    /// </summary>
    public static long EstimateFeeBufferSat(long amountSat)
    {
        if (amountSat <= 0)
            return 2;
        return Math.Max(2, (amountSat + 99) / 100); // integer ceil(amountSat / 100)
    }

    /// <summary>
    /// Forward amount to retry with when the quoted reserve exceeds the buffer:
    /// totalMinted - actual reserve. Null when no smaller positive amount exists,
    /// i.e. the adjustment cannot converge.
    /// </summary>
    public static long? ReducedForwardSat(long totalMintedSat, long feeReserve, long currentForwardSat)
    {
        var reduced = totalMintedSat - feeReserve;
        if (reduced <= 0 || reduced >= currentForwardSat)
            return null;
        return reduced;
    }

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
