#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;

/// <summary>
/// Turns Fio JSON movements (accountStatement.transactionList.transaction)
/// into <see cref="ConfirmedPayment"/> records. Pure logic - unit-testable
/// without the transport.
///
/// Column map per Fio API Bankovnictví v1.9 (5.3.1.6): column22 = movement
/// id (dedup), column1 = amount, column14 = currency, column27 = payer
/// reference ("Reference plátce" - carries the SEPA end-to-end id),
/// column5 = variable symbol, column16 = message for the recipient.
/// Reference resolution order: column27, then column5, then a QR-…
/// end-to-end id found inside column16.
/// </summary>
public partial class FioTransactionProcessor
{
    [GeneratedRegex("QR-[0-9a-fA-F]{32}")]
    private static partial Regex EndToEndInMessage();

    public IReadOnlyList<ConfirmedPayment> Parse(JsonDocument document)
    {
        var results = new List<ConfirmedPayment>();
        if (!document.RootElement.TryGetProperty("accountStatement", out var statement)
            || !statement.TryGetProperty("transactionList", out var list)
            || list.ValueKind != JsonValueKind.Object
            || !list.TryGetProperty("transaction", out var transactions)
            || transactions.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var transaction in transactions.EnumerateArray())
        {
            var payment = ToConfirmedPayment(transaction);
            if (payment is not null)
                results.Add(payment);
        }

        return results;
    }

    public ConfirmedPayment? ToConfirmedPayment(JsonElement transaction)
    {
        var amount = GetDecimal(transaction, "column1");
        if (amount is null or <= 0)
            return null; // outgoing or malformed - only credits can confirm

        var currency = GetString(transaction, "column14");
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var movementId = GetString(transaction, "column22");
        if (string.IsNullOrWhiteSpace(movementId))
            return null;

        var reference = ResolveReference(transaction);
        if (reference is null)
            return null; // no reference we could ever match - not ours

        return new ConfirmedPayment(
            reference,
            amount.Value,
            currency!.ToUpperInvariant(),
            transaction.GetRawText(),
            DedupKey: $"fio:{movementId}");
    }

    private static string? ResolveReference(JsonElement transaction)
    {
        var payerReference = GetString(transaction, "column27");
        if (!string.IsNullOrWhiteSpace(payerReference))
            return payerReference!.Trim();

        var variableSymbol = GetString(transaction, "column5");
        if (!string.IsNullOrWhiteSpace(variableSymbol))
            return variableSymbol!.Trim();

        var message = GetString(transaction, "column16");
        if (!string.IsNullOrWhiteSpace(message))
        {
            var match = EndToEndInMessage().Match(message!);
            if (match.Success)
                return match.Value;
        }

        return null;
    }

    private static string? GetString(JsonElement transaction, string column)
        => transaction.TryGetProperty(column, out var cell)
           && cell.ValueKind == JsonValueKind.Object
           && cell.TryGetProperty("value", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static decimal? GetDecimal(JsonElement transaction, string column)
    {
        if (!transaction.TryGetProperty(column, out var cell)
            || cell.ValueKind != JsonValueKind.Object
            || !cell.TryGetProperty("value", out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
