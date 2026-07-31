using BTCPayServer.Plugins.SepaInstantQr.Services.Qr;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// Golden-file tests: the exact payload strings for fixed inputs. Grounded
/// in the primary specs (docs/research/qr-formats.md) - the PayMe /m/
/// example from the SBA Payment Link Standard v2.0, the SPD spec at
/// qr-platba.cz and EPC069-12 v3.1.
/// </summary>
public class QrPayloadGoldenTests
{
    [Fact]
    public void PayMe_m_type_matches_the_sba_spec_example_shape()
    {
        var builder = new PayMeV2PayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "SK6807200002891987426353",
            Beneficiary: "The Best Cafes ltd",
            Amount: 200.30m,
            Reference: "QR-ab29e346f1d841c8a95a63d857490818",
            Message: "Cafe on the corner Zilina"));

        Assert.Equal(
            "https://payme.sk/2/m/PME?IBAN=SK6807200002891987426353&AM=200.30&CC=EUR" +
            "&PI=QR-ab29e346f1d841c8a95a63d857490818&CN=The+Best+Cafes+ltd&MSG=Cafe+on+the+corner+Zilina",
            payload);
    }

    [Fact]
    public void PayMe_normalizes_diacritics_and_encodes_spaces()
    {
        var builder = new PayMeV2PayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "SK68 0720 0002 8919 8742 6353",
            Beneficiary: "Kaviareň Čajovňa s.r.o.",
            Amount: 1m,
            Reference: "QR-00000000000000000000000000000001",
            Message: null));

        Assert.Contains("CN=Kaviaren+Cajovna+s.r.o.", payload);
        Assert.Contains("IBAN=SK6807200002891987426353", payload);
        Assert.Contains("AM=1.00", payload);
        Assert.DoesNotContain("MSG=", payload);
    }

    [Fact]
    public void Spd_payload_uses_the_request_currency_for_czk()
    {
        var builder = new SpdPayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "CZ5855000000001265098001",
            Beneficiary: "Petr Dvořák",
            Amount: 480.55m,
            Reference: "1234567890",
            Message: null,
            Currency: "CZK"));

        Assert.Contains("*CC:CZK*", payload);
        Assert.Contains("AM:480.55", payload);
    }

    [Theory]
    [InlineData("SK", "EUR", true)]
    [InlineData("SK", "CZK", false)]
    [InlineData("CZ", "EUR", true)]
    [InlineData("CZ", "CZK", true)]
    [InlineData("EU", "EUR", true)]
    [InlineData("EU", "CZK", false)]
    [InlineData("CZ", "USD", false)]
    public void Profile_currency_gate(string profile, string currency, bool expected)
    {
        Assert.Equal(expected,
            BTCPayServer.Plugins.SepaInstantQr.PaymentHandler.SepaPaymentMethodHandler.SupportsCurrency(profile, currency));
    }

    [Fact]
    public void Spd_payload_carries_vs_and_instant_payment_flag()
    {
        var builder = new SpdPayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "CZ5855000000001265098001",
            Beneficiary: "Petr Dvořák",
            Amount: 480.55m,
            Reference: "1234567890",
            Message: "Platba za zboží",
            Bic: "RZBCCZPP"));

        Assert.Equal(
            "SPD*1.0*ACC:CZ5855000000001265098001+RZBCCZPP*AM:480.55*CC:EUR" +
            "*X-VS:1234567890*RN:PETR DVORAK*MSG:PLATBA ZA ZBOZI*PT:IP",
            payload);
    }

    [Fact]
    public void Epc_payload_matches_the_v2_line_layout()
    {
        var builder = new EpcQrPayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "DE71110220330123456789",
            Beneficiary: "Franz Mustermänn",
            Amount: 12.30m,
            Reference: "QR-ab29e346f1d841c8a95a63d857490818",
            Message: null));

        Assert.Equal(
            "BCD\n002\n1\nSCT\n\nFranz Mustermänn\nDE71110220330123456789\nEUR12.30\n\n\n" +
            "QR-ab29e346f1d841c8a95a63d857490818",
            payload);
    }

    [Fact]
    public void Long_values_are_truncated_to_the_standard_limits()
    {
        var longName = new string('A', 100);
        var payme = new PayMeV2PayloadBuilder().Build(new SepaQrRequest(
            "SK6807200002891987426353", longName, 1m, "QR-00000000000000000000000000000001", null));
        Assert.Contains("CN=" + new string('A', 70) + "&", payme + "&");

        var epc = new EpcQrPayloadBuilder().Build(new SepaQrRequest(
            "DE71110220330123456789", longName, 1m, "QR-00000000000000000000000000000001", null));
        Assert.Contains("\n" + new string('A', 70) + "\n", epc);

        var spd = new SpdPayloadBuilder().Build(new SepaQrRequest(
            "CZ5855000000001265098001", longName, 1m, "1", null));
        Assert.Contains("RN:" + new string('A', 35) + "*", spd);
    }
}
