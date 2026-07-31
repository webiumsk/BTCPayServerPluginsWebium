using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>
/// CZ profile: Short Payment Descriptor 1.0 ("QR Platba",
/// qr-platba.cz/pro-vyvojare/specifikace-formatu). The reference travels as
/// X-VS (numeric variable symbol); PT:IP requests instant processing.
/// </summary>
public class SpdPayloadBuilder : IQrPayloadBuilder
{
    public string Profile => "CZ";

    public string Build(SepaQrRequest request)
    {
        var sb = new StringBuilder("SPD*1.0*");
        sb.Append("ACC:").Append(IbanValidator.Normalize(request.Iban));
        if (!string.IsNullOrWhiteSpace(request.Bic))
            sb.Append('+').Append(request.Bic!.Trim().ToUpperInvariant());
        sb.Append('*');
        sb.Append("AM:").Append(QrText.Amount(request.Amount)).Append('*');
        sb.Append("CC:").Append(request.Currency.ToUpperInvariant()).Append('*');
        sb.Append("X-VS:").Append(request.Reference).Append('*');
        sb.Append("RN:").Append(EncodeValue(request.Beneficiary, 35)).Append('*');
        if (!string.IsNullOrWhiteSpace(request.Message))
            sb.Append("MSG:").Append(EncodeValue(request.Message, 60)).Append('*');
        sb.Append("PT:IP");
        return sb.ToString();
    }

    /// <summary>
    /// Uppercase ASCII per the spec's efficiency alphabet
    /// (0-9 A-Z space $ % * + - . / :); '*' is the pair separator so it is
    /// URL-encoded, other disallowed characters are dropped.
    /// </summary>
    private static string EncodeValue(string value, int maxLength)
    {
        var ascii = QrText.Truncate(QrText.ToAscii(value).Trim().ToUpperInvariant(), maxLength);
        var sb = new StringBuilder(ascii.Length);
        foreach (var c in ascii)
        {
            if (c == '*')
                sb.Append("%2A");
            else if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '$' or '%' or '+' or '-' or '.' or '/' or ':')
                sb.Append(c);
        }

        return sb.ToString();
    }
}
