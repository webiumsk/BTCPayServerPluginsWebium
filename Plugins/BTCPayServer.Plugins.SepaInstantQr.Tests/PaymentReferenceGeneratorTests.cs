using System.Text.RegularExpressions;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public partial class PaymentReferenceGeneratorTests
{
    [GeneratedRegex("^QR-[0-9a-f]{32}$")]
    private static partial Regex EndToEndPattern();

    [GeneratedRegex("^[1-9][0-9]{9}$")]
    private static partial Regex VariableSymbolPattern();

    [Fact]
    public void EndToEnd_ids_are_nop_shaped()
    {
        for (var i = 0; i < 100; i++)
        {
            var reference = PaymentReferenceGenerator.NewEndToEndId();
            Assert.Matches(EndToEndPattern(), reference);
            Assert.True(reference.Length <= 35); // PI / EndToEndId limit
        }
    }

    [Fact]
    public void Variable_symbols_are_ten_digits_without_leading_zero()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.Matches(VariableSymbolPattern(), PaymentReferenceGenerator.NewVariableSymbol());
        }
    }

    [Fact]
    public void References_are_unique()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(seen.Add(PaymentReferenceGenerator.NewEndToEndId()));
        }
    }
}
