using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class NopNotificationProcessorTests
{
    // The notification example from the SBA Standard for Push Payment
    // Notification 1.1 - dataIntegrityHash is the published golden vector.
    private const string GoldenPayload = """
        {
          "transactionStatus": "ACCC",
          "endToEndId": "QR-ab29e346f1d841c8a95a63d857490818",
          "transactionAmount": {"currency": "EUR", "amount": "123.45"},
          "dataIntegrityHash": "b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae",
          "creditorAccount": {"iban": "SK4811000000002944116480"},
          "creditorName": "Merchant Name, sro",
          "receivedAt": "2025-08-16T20:43:37.562311Z"
        }
        """;

    private readonly NopNotificationProcessor _processor = new();

    [Fact]
    public void Parses_the_standard_example()
    {
        var notification = _processor.Parse(GoldenPayload);

        Assert.NotNull(notification);
        Assert.Equal("ACCC", notification.TransactionStatus);
        Assert.Equal("123.45", notification.AmountRaw);
        Assert.Equal(123.45m, notification.Amount);
        Assert.Equal("EUR", notification.Currency);
        Assert.Equal("QR-ab29e346f1d841c8a95a63d857490818", notification.EndToEndId);
        Assert.Equal("SK4811000000002944116480", notification.CreditorIban);
    }

    [Fact]
    public void Golden_hash_vector_verifies()
    {
        // Annex B example: SK4811000000002944116480|123.45|EUR|QR-... →
        // b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae
        Assert.Equal(
            "b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae",
            NopNotificationProcessor.ComputeHash(
                "SK4811000000002944116480", "123.45", "EUR", "QR-ab29e346f1d841c8a95a63d857490818"));

        var notification = _processor.Parse(GoldenPayload)!;
        Assert.True(_processor.VerifyHash(notification, "SK4811000000002944116480"));
    }

    [Fact]
    public void Fallback_iban_is_used_when_creditor_account_is_absent()
    {
        var payload = GoldenPayload.Replace("\"creditorAccount\": {\"iban\": \"SK4811000000002944116480\"},", "");
        var notification = _processor.Parse(payload)!;

        Assert.Null(notification.CreditorIban);
        Assert.True(_processor.VerifyHash(notification, "SK4811000000002944116480"));
        Assert.False(_processor.VerifyHash(notification, "SK6807200002891987426353"));
    }

    [Fact]
    public void Settled_notification_maps_to_a_confirmed_payment()
    {
        var notification = _processor.Parse(GoldenPayload)!;
        var confirmed = _processor.ToConfirmedPayment(notification);

        Assert.NotNull(confirmed);
        Assert.Equal("QR-ab29e346f1d841c8a95a63d857490818", confirmed.Reference);
        Assert.Equal(123.45m, confirmed.Amount);
        Assert.Equal("EUR", confirmed.Currency);
        Assert.Null(confirmed.IntegrityFailure);
        Assert.Equal("SK4811000000002944116480", confirmed.CreditorIban);
        Assert.NotNull(confirmed.NopIntegrity);
        Assert.Equal("nop:QR-ab29e346f1d841c8a95a63d857490818:2025-08-16T20:43:37.562311Z", confirmed.DedupKey);
    }

    [Fact]
    public void Dedup_key_is_stable_for_duplicate_deliveries()
    {
        var first = _processor.ToConfirmedPayment(_processor.Parse(GoldenPayload)!)!;
        var second = _processor.ToConfirmedPayment(_processor.Parse(GoldenPayload)!)!;

        Assert.Equal(first.DedupKey, second.DedupKey);
    }

    [Fact]
    public void Tampered_amount_preserves_evidence_for_snapshot_bound_matching()
    {
        var tampered = GoldenPayload.Replace("\"amount\": \"123.45\"", "\"amount\": \"1.00\"");
        var notification = _processor.Parse(tampered)!;
        var confirmed = _processor.ToConfirmedPayment(notification);

        Assert.NotNull(confirmed);
        Assert.NotNull(confirmed.NopIntegrity);
        Assert.Equal("1.00", confirmed.NopIntegrity.AmountRaw);
        Assert.Equal("b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae", confirmed.NopIntegrity.DataIntegrityHash);
    }

    [Fact]
    public void Non_accc_statuses_are_ignored()
    {
        var pending = GoldenPayload.Replace("\"ACCC\"", "\"PDNG\"");
        var notification = _processor.Parse(pending)!;

        Assert.Null(_processor.ToConfirmedPayment(notification));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"transactionStatus\": \"ACCC\"}")]
    public void Malformed_payloads_return_null(string payload)
    {
        Assert.Null(_processor.Parse(payload));
    }
}
