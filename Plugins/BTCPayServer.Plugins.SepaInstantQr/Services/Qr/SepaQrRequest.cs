namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>Input for a QR payload builder. Amount is EUR with 2 decimals.</summary>
public record SepaQrRequest(
    string Iban,
    string Beneficiary,
    decimal Amount,
    string Reference,
    string? Message,
    string? Bic = null);
