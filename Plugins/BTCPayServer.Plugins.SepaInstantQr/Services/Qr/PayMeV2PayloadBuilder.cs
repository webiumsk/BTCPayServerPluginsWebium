using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>
/// SK profile: SBA Payment Link Standard v2.0, payme.sk implementation,
/// Type /m/ (dynamic QR at the point of interaction, SCT Inst, non-editable).
/// See docs/research/qr-formats.md. Example:
/// https://payme.sk/2/m/PME?IBAN=...&AM=12.34&CC=EUR&PI=QR-...&CN=...&MSG=...
/// </summary>
public class PayMeV2PayloadBuilder : IQrPayloadBuilder
{
    public string Profile => "SK";

    public string Build(SepaQrRequest request)
    {
        var sb = new StringBuilder("https://payme.sk/2/m/PME?");
        sb.Append("IBAN=").Append(IbanValidator.Normalize(request.Iban));
        sb.Append("&AM=").Append(QrText.Amount(request.Amount));
        sb.Append("&CC=EUR");
        sb.Append("&PI=").Append(EncodeParam(request.Reference, 35));
        sb.Append("&CN=").Append(EncodeParam(request.Beneficiary, 70));
        if (!string.IsNullOrWhiteSpace(request.Message))
            sb.Append("&MSG=").Append(EncodeParam(request.Message, 140));
        return sb.ToString();
    }

    /// <summary>
    /// Annex A recommended set, ASCII-normalized; spaces encoded as '+'
    /// (the standard's preferred readable encoding), the rest URL-escaped.
    /// </summary>
    private static string EncodeParam(string value, int maxLength)
    {
        var ascii = QrText.Truncate(QrText.ToAscii(value).Trim(), maxLength);
        var sb = new StringBuilder(ascii.Length);
        foreach (var c in ascii)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '?' or ':' or '(' or ')' or '.' or ',' or '\'')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('+');
            else if (c == '+')
                sb.Append("%2B");
            else
                sb.Append('%').Append(((int)c).ToString("X2"));
        }

        return sb.ToString();
    }
}
