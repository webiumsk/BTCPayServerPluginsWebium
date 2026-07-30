using BTCPayServer.Plugins.SepaInstantQr.Services;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class IbanValidatorTests
{
    [Theory]
    [InlineData("SK6807200002891987426353")]           // SBA spec example
    [InlineData("SK4811000000002944116480")]           // SBA notification standard example
    [InlineData("CZ5855000000001265098001")]           // SPD spec example
    [InlineData("DE71110220330123456789")]             // EPC spec example
    [InlineData("FR1420041010050500013M02606")]        // EPC spec example (alphanumeric BBAN)
    [InlineData("sk68 0720 0002 8919 8742 6353")]      // lowercase + spaces normalize
    public void Valid_ibans_pass(string iban)
    {
        Assert.True(IbanValidator.IsValid(iban));
    }

    [Theory]
    [InlineData("SK6807200002891987426354")]  // checksum off by one
    [InlineData("SK68072000028919874263")]    // SK must be 24 chars
    [InlineData("CZ58550000000012650980011")] // CZ must be 24 chars
    [InlineData("6807200002891987426353SK")]  // country code not first
    [InlineData("SKAB07200002891987426353")]  // check digits not numeric
    [InlineData("SK680720000289198742635!")]  // invalid character
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SK123")]                     // too short
    public void Invalid_ibans_fail(string? iban)
    {
        Assert.False(IbanValidator.IsValid(iban));
    }

    [Fact]
    public void Normalize_strips_whitespace_and_uppercases()
    {
        Assert.Equal("SK6807200002891987426353", IbanValidator.Normalize(" sk68 0720 0002\t8919 8742 6353 "));
    }
}
