using System.Text.Json;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// Fio JSON parsing per API Bankovnictví v1.9 (5.3.1.6): column22 = movement
/// id, column1 = amount, column14 = currency, column27 = payer reference
/// (SEPA end-to-end id), column5 = VS, column16 = message. The sample shapes
/// mirror the documentation examples ({"columnN":{"value":...,"name":...}}).
/// </summary>
public class FioTransactionProcessorTests
{
    private static readonly FioTransactionProcessor Processor = new();

    private static string Cell(string column, object value)
    {
        var json = value is string s ? JsonSerializer.Serialize(s) : value.ToString();
        return $"\"{column}\":{{\"value\":{json},\"name\":\"x\",\"id\":1}}";
    }

    private static JsonDocument Statement(params string[] transactions)
        => JsonDocument.Parse(
            "{\"accountStatement\":{\"info\":{\"iban\":\"SK6883300000002600000000\",\"currency\":\"EUR\"}," +
            "\"transactionList\":{\"transaction\":[" + string.Join(',', transactions) + "]}}}");

    private static string Transaction(params string[] cells) => "{" + string.Join(',', cells) + "}";

    [Fact]
    public void Prefers_the_payer_reference_column()
    {
        using var doc = Statement(Transaction(
            Cell("column22", 1148734530),
            Cell("column1", 12.50),
            Cell("column14", "EUR"),
            Cell("column27", "QR-ab29e346f1d841c8a95a63d857490818"),
            Cell("column5", "1234567890"),
            Cell("column16", "lunch")));

        var payments = Processor.Parse(doc);

        var payment = Assert.Single(payments);
        Assert.Equal("QR-ab29e346f1d841c8a95a63d857490818", payment.Reference);
        Assert.Equal(12.50m, payment.Amount);
        Assert.Equal("EUR", payment.Currency);
        Assert.Equal("fio:1148734530", payment.DedupKey);
        Assert.Null(payment.IntegrityFailure);
    }

    [Fact]
    public void Falls_back_to_the_variable_symbol()
    {
        using var doc = Statement(Transaction(
            Cell("column22", 2),
            Cell("column1", 480.55),
            Cell("column14", "CZK"),
            Cell("column5", "1234567890")));

        var payment = Assert.Single(Processor.Parse(doc));
        Assert.Equal("1234567890", payment.Reference);
        Assert.Equal("CZK", payment.Currency);
    }

    [Fact]
    public void Extracts_an_end_to_end_id_from_the_message()
    {
        using var doc = Statement(Transaction(
            Cell("column22", 3),
            Cell("column1", 5),
            Cell("column14", "EUR"),
            Cell("column16", "Payment QR-ab29e346f1d841c8a95a63d857490818 thanks")));

        var payment = Assert.Single(Processor.Parse(doc));
        Assert.Equal("QR-ab29e346f1d841c8a95a63d857490818", payment.Reference);
    }

    [Fact]
    public void Skips_outgoing_and_unmatchable_movements()
    {
        using var doc = Statement(
            Transaction( // outgoing
                Cell("column22", 4),
                Cell("column1", -15.00),
                Cell("column14", "EUR"),
                Cell("column27", "QR-ab29e346f1d841c8a95a63d857490818")),
            Transaction( // no reference anywhere (e.g. interest credit)
                Cell("column22", 5),
                Cell("column1", 0.02),
                Cell("column14", "EUR")),
            Transaction( // null cells like the documentation examples
                "\"column22\":null", "\"column1\":null", "\"column14\":null"));

        Assert.Empty(Processor.Parse(doc));
    }

    [Fact]
    public void Handles_an_empty_transaction_list()
    {
        using var doc = JsonDocument.Parse(
            "{\"accountStatement\":{\"info\":{},\"transactionList\":{\"transaction\":[]}}}");
        Assert.Empty(Processor.Parse(doc));

        using var noList = JsonDocument.Parse("{\"accountStatement\":{\"info\":{}}}");
        Assert.Empty(Processor.Parse(noList));
    }
}
