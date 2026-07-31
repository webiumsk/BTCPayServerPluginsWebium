#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.SepaInstantQr.Services;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

/// <summary>
/// One NOP payment notification (Standard for Push Payment Notification 1.1
/// / NOP Services API). Amount stays the RAW string from the payload - the
/// dataIntegrityHash is computed over it verbatim.
/// </summary>
public sealed record NopNotification(
    string TransactionStatus,
    string AmountRaw,
    string Currency,
    string EndToEndId,
    string DataIntegrityHash,
    string? CreditorIban,
    string? Timestamp,
    string RawJson)
{
    public decimal Amount => decimal.Parse(AmountRaw, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Transport-independent parsing + integrity verification + mapping to
/// <see cref="ConfirmedPayment"/> - shared by the MQTT listener and the
/// NOP Lite REST poller. Pure logic, fully unit-testable.
/// </summary>
public class NopNotificationProcessor
{
    public NopNotification? Parse(string json)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var status = GetString(root, "transactionStatus");
        var endToEndId = GetString(root, "endToEndId") ?? GetString(root, "endToEndID");
        var hash = GetString(root, "dataIntegrityHash");
        string? amount = null;
        string? currency = null;
        if (root.TryGetProperty("transactionAmount", out var amountObj) && amountObj.ValueKind == JsonValueKind.Object)
        {
            amount = GetString(amountObj, "amount");
            currency = GetString(amountObj, "currency");
        }

        string? creditorIban = null;
        if (root.TryGetProperty("creditorAccount", out var creditor) && creditor.ValueKind == JsonValueKind.Object)
            creditorIban = GetString(creditor, "iban");

        var timestamp = GetString(root, "receivedAt") ?? GetString(root, "happened_at") ?? GetString(root, "happenedAt");

        if (status is null || endToEndId is null || hash is null || amount is null || currency is null)
            return null;

        if (!decimal.TryParse(amount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            return null;

        return new NopNotification(status, amount, currency, endToEndId, hash, creditorIban, timestamp, json);
    }

    /// <summary>
    /// SBA Standard for Push Payment Notification, Annex B: SHA-256 lowercase
    /// hex of "IBAN|amount|currency|endToEndId". The amount is hashed exactly
    /// as delivered; the IBAN comes from creditorAccount, else the merchant's
    /// configured IBAN (same account).
    /// </summary>
    public static string ComputeHash(string iban, string amountRaw, string currency, string endToEndId)
    {
        var input = $"{IbanValidator.Normalize(iban)}|{amountRaw}|{currency}|{endToEndId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    public bool VerifyHash(NopNotification notification, string fallbackIban)
    {
        var iban = string.IsNullOrEmpty(notification.CreditorIban) ? fallbackIban : notification.CreditorIban;
        var expected = ComputeHash(iban, notification.AmountRaw, notification.Currency, notification.EndToEndId);
        return string.Equals(expected, notification.DataIntegrityHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a notification to a ConfirmedPayment; null when the notification
    /// is not a settled credit (only ACCC counts). A failed integrity check
    /// still produces a payment - flagged so matching routes it to manual
    /// review instead of settling.
    /// </summary>
    public ConfirmedPayment? ToConfirmedPayment(NopNotification notification, string fallbackIban)
    {
        if (!string.Equals(notification.TransactionStatus, "ACCC", StringComparison.OrdinalIgnoreCase))
            return null;

        var integrityFailure = VerifyHash(notification, fallbackIban)
            ? null
            : "dataIntegrityHash mismatch";

        return new ConfirmedPayment(
            notification.EndToEndId,
            notification.Amount,
            notification.Currency,
            notification.RawJson,
            DedupKey: $"nop:{notification.EndToEndId}:{notification.Timestamp ?? ""}",
            IntegrityFailure: integrityFailure);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
