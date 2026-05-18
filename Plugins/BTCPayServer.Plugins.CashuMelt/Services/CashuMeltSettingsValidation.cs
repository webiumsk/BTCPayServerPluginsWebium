#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Verifies the merchant payout Lightning address resolves via LNURL-pay (used after melt, not at customer checkout).
    /// </summary>
    public static async Task ValidateLightningAddressResolvableAsync(
        string lightningAddress,
        LightningAddressResolver resolver,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lightningAddress) || !lightningAddress.Contains('@'))
            throw new InvalidOperationException("Lightning address must be in user@domain format.");

        try
        {
            await resolver.ResolveInvoiceAsync(lightningAddress.Trim(), 1, long.MaxValue / 2, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not verify Lightning address for merchant payout: {ex.Message}. " +
                "This address receives funds after a Cashu payment is melted; it is not the store's checkout Lightning node. " +
                "Ensure LNURL-pay is enabled on the recipient wallet.",
                ex);
        }
    }
}
