namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>
/// Input for a QR payload builder. Amount is EUR with 2 decimals.
/// PaymentDate feeds formats with a due-date field (PAY by square);
/// null means "today" - tests pass a fixed date for determinism.
/// </summary>
public record SepaQrRequest(
    string Iban,
    string Beneficiary,
    decimal Amount,
    string Reference,
    string? Message,
    string? Bic = null,
    System.DateOnly? PaymentDate = null,
    string Currency = "EUR");
