#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

/// <summary>
/// PAY by square payload (SBA "PAY by square specifications" 1.2.0,
/// https://bysquare.com/) - the established Slovak bank-app QR standard.
/// Pipeline: tab-separated payment data -> CRC32 (little-endian) prepended
/// -> raw LZMA1 (lc=3, lp=0, pb=2, dict=128KiB, end marker) -> 2-byte
/// header 0x00 0x00 + uint16 LE length of CRC+data -> 5-bit base32hex
/// ("0-9A-V"). Golden vectors in tests are generated with the same xz
/// pipeline the production-proven satflux implementation uses.
///
/// The NOP payment reference travels in OriginatorsReferenceInformation;
/// PayMe stays the recommended SK variant for NOP confirmation because its
/// PI field is defined to map to the SEPA end-to-end id.
/// </summary>
public class PayBySquarePayloadBuilder : IQrPayloadBuilder
{
    public const string ProfileKey = "SK-BYSQUARE";
    private const string Base32HexAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

    public string Profile => ProfileKey;

    public string Build(SepaQrRequest request)
    {
        var date = request.PaymentDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dataString = string.Join('\t',
            "",                                                     // InvoiceID
            "1",                                                    // number of payments
            "1",                                                    // payment option: payment order
            request.Amount.ToString("0.##", CultureInfo.InvariantCulture),
            "EUR",
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), // due date
            "",                                                     // variable symbol
            "",                                                     // constant symbol
            "",                                                     // specific symbol
            Clean(request.Reference, 35),                           // originators reference information (SEPA e2e id length)
            Clean(request.Message, 60),                             // payment note
            "1",                                                    // number of bank accounts
            request.Iban,
            request.Bic ?? "",
            "0",                                                    // standing order extension
            "0",                                                    // direct debit extension
            Clean(request.Beneficiary, 70),
            "",                                                     // beneficiary address line 1
            "");                                                    // beneficiary address line 2

        var data = Encoding.UTF8.GetBytes(dataString);
        var payload = new byte[4 + data.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), Crc32(data));
        data.CopyTo(payload, 4);

        var compressed = Lzma1Compress(payload);

        var framed = new byte[4 + compressed.Length];
        framed[0] = 0x00; // bysquare type: Pay
        framed[1] = 0x00; // version 0, document type 0
        BitConverter.TryWriteBytes(framed.AsSpan(2, 2), (ushort)payload.Length);
        compressed.CopyTo(framed, 4);

        return ToBase32Hex(framed);
    }

    /// <summary>
    /// bysquare carries UTF-8, so diacritics stay (matching the reference
    /// implementation) - only field separators are stripped and length capped.
    /// </summary>
    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var cleaned = string.Join(' ',
            value.Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static byte[] Lzma1Compress(byte[] payload)
    {
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.SetCoderProperties(
            [
                SevenZip.CoderPropID.DictionarySize,
                SevenZip.CoderPropID.LitContextBits,
                SevenZip.CoderPropID.LitPosBits,
                SevenZip.CoderPropID.PosStateBits,
                SevenZip.CoderPropID.NumFastBytes,
                SevenZip.CoderPropID.MatchFinder,
                SevenZip.CoderPropID.Algorithm,
                SevenZip.CoderPropID.EndMarker,
            ],
            [128 * 1024, 3, 0, 2, 64, "bt4", 2, true]);

        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();
        encoder.Code(input, output, payload.Length, -1, null);
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return ~crc;
    }

    private static string ToBase32Hex(byte[] raw)
    {
        var result = new StringBuilder((raw.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in raw)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(Base32HexAlphabet[(buffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            result.Append(Base32HexAlphabet[(buffer << (5 - bits)) & 0x1F]);
        return result.ToString();
    }
}
