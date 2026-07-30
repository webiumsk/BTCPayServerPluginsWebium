using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// The matching rule shared by every confirmation backend: exact amount by
/// default, configurable tolerance, currency must match; anything else is a
/// manual-review verdict, never an auto-settle.
/// </summary>
public class SepaMatchingEvaluateTests
{
    private static ConfirmedPayment Payment(decimal amount, string currency = "EUR")
        => new("QR-00000000000000000000000000000001", amount, currency, null, null);

    [Fact]
    public void Exact_amount_matches()
    {
        Assert.Null(SepaMatchingService.Evaluate(100.00m, "EUR", Payment(100.00m), 0m));
    }

    [Fact]
    public void Overpayment_matches()
    {
        Assert.Null(SepaMatchingService.Evaluate(100.00m, "EUR", Payment(120.00m), 0m));
    }

    [Fact]
    public void Short_payment_is_flagged()
    {
        var verdict = SepaMatchingService.Evaluate(100.00m, "EUR", Payment(99.99m), 0m);
        Assert.NotNull(verdict);
        Assert.Contains("amount too low", verdict);
    }

    [Fact]
    public void Tolerance_allows_slightly_short_payments()
    {
        Assert.Null(SepaMatchingService.Evaluate(100.00m, "EUR", Payment(99.99m), 0.05m));
        Assert.NotNull(SepaMatchingService.Evaluate(100.00m, "EUR", Payment(99.90m), 0.05m));
    }

    [Fact]
    public void Currency_mismatch_is_flagged()
    {
        var verdict = SepaMatchingService.Evaluate(100.00m, "EUR", Payment(100.00m, "CZK"), 0m);
        Assert.NotNull(verdict);
        Assert.Contains("currency mismatch", verdict);
    }

    [Fact]
    public void Currency_comparison_is_case_insensitive()
    {
        Assert.Null(SepaMatchingService.Evaluate(100.00m, "EUR", Payment(100.00m, "eur"), 0m));
    }
}
