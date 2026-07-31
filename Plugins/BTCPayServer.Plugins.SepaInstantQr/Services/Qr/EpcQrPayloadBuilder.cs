using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>
/// EU-generic profile: EPC069-12 v3.1 "girocode". Version 002, UTF-8, no
/// BIC, unstructured remittance carries the payment reference. Elements are
/// LF-separated in fixed order; trailing empty elements are omitted.
/// </summary>
public class EpcQrPayloadBuilder : IQrPayloadBuilder
{
    public string Profile => "EU";

    public string Build(SepaQrRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("BCD\n");
        sb.Append("002\n");
        sb.Append("1\n");
        sb.Append("SCT\n");
        sb.Append('\n'); // BIC omitted (optional in V2 within EEA)
        sb.Append(QrText.Truncate(request.Beneficiary.Trim(), 70)).Append('\n');
        sb.Append(IbanValidator.Normalize(request.Iban)).Append('\n');
        sb.Append("EUR").Append(QrText.Amount(request.Amount)).Append('\n');
        sb.Append('\n'); // Purpose omitted
        sb.Append('\n'); // Structured remittance omitted (reference is not ISO 11649)
        sb.Append(QrText.Truncate(request.Reference, 140));
        // Last populated element carries no trailing separator.
        return sb.ToString();
    }
}
