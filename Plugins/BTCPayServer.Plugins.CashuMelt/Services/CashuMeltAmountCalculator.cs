#nullable enable
using System;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Maps BTCPay invoice amounts to mint quote amounts (sat or usd cents).</summary>
public static class CashuMeltAmountCalculator
{
    public static long ComputeMintAmount(string invoiceCurrency, decimal invoicePrice, decimal promptDue, string settingsUnit)
    {
        var unit = settingsUnit ?? "sat";

        if (string.Equals(invoiceCurrency, "SATS", StringComparison.OrdinalIgnoreCase))
        {
            if (unit == "usd")
                return Math.Max(1, (long)Math.Round(promptDue * 100));

            var fromPrice = (long)Math.Round(invoicePrice);
            if (fromPrice > 0)
                return fromPrice;

            return Math.Max(1, (long)Math.Round(promptDue * 100_000_000));
        }

        if (unit == "usd")
            return Math.Max(1, (long)Math.Round(promptDue * 100));

        var amountSats = (long)Math.Round(promptDue * 100_000_000);
        return amountSats < 1 ? 1 : amountSats;
    }
}
