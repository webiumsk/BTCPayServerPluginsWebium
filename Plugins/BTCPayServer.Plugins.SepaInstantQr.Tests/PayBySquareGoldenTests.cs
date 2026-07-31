using System;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;
using BTCPayServer.Plugins.SepaInstantQr.Services.Qr;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// Golden-file tests for the PAY by square payload. Reference payloads were
/// generated with the same xz pipeline the production-proven satflux
/// implementation uses (raw LZMA1 lc=3,lp=0,pb=2,dict=128KiB, CRC32 LE
/// prefix, 0x0000 + uint16 LE length header, base32hex) and cross-checked
/// against python's lzma FORMAT_RAW encoder - both produce identical bytes.
/// A fixed PaymentDate keeps the payloads deterministic.
/// </summary>
public class PayBySquareGoldenTests
{
    private static readonly DateOnly FixedDate = new(2026, 7, 31);

    [Fact]
    public void Encodes_reference_note_and_decimal_amount()
    {
        var builder = new PayBySquarePayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "SK4811000000002944116480",
            Beneficiary: "Test s.r.o.",
            Amount: 123.45m,
            Reference: "QR-ab29e346f1d841c8a95a63d857490818",
            Message: "Invoice 42",
            PaymentDate: FixedDate));

        Assert.Equal(
            "0007O0007UPGB3UG9C98Q7OFUG5019KRS79LH1Q1EU7FI5KQ20O5GOFB59AGV3ELOU5SP060MT9FGK43VPLMS6PN4E1MEER752AG0" +
            "BB4M33UDAH1SPQM36038AL716RVIN6VTK9S6K17C2PFSPBIA4K27V7RO8HGHO0VBDEMHPVT49RA1FSP5FNU1VK1VVS53JG00",
            payload);
    }

    [Fact]
    public void Encodes_bic_and_whole_amount_without_decimals()
    {
        var builder = new PayBySquarePayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "SK6807200002891987426353",
            Beneficiary: "My Company s.r.o.",
            Amount: 10m,
            Reference: "QR-00000000000000000000000000000001",
            Message: null,
            Bic: "TATRSK21",
            PaymentDate: FixedDate));

        Assert.Equal(
            "0007O0006KL00RO09C98Q7MBC7D8QJG1MF0EK9C5BKTG0JF5LA6BP717VV4O1JM143SPODULVCV57V8G37LF6NE9J0D0EEQDAGF6UQ" +
            "4ECSUSUB0M234TPH83K41H25784EEILCF7THVOPNJMGUJAJD797VVC7QK0",
            payload);
    }

    [Fact]
    public void Keeps_utf8_diacritics_full_pipeline_round_trip()
    {
        // LZMA encoders may legitimately pick different (equally valid)
        // match sequences, so a byte-for-byte golden only works when our
        // encoder happens to agree with the xz reference (the ASCII vectors
        // above do). For UTF-8 input we instead decode the whole pipeline
        // back: base32hex -> header -> raw LZMA1 -> CRC32 -> data string.
        var builder = new PayBySquarePayloadBuilder();
        var payload = builder.Build(new SepaQrRequest(
            Iban: "SK4811000000002944116480",
            Beneficiary: "Kaviareň Mlynská dolina s.r.o.",
            Amount: 5m,
            Reference: "QR-ab29e346f1d841c8a95a63d857490818",
            Message: "Zmrzlina a káva",
            PaymentDate: FixedDate));

        var raw = FromBase32Hex(payload);
        Assert.Equal(0x00, raw[0]); // bysquare type Pay, version 0
        Assert.Equal(0x00, raw[1]);
        var declaredLength = raw[2] | (raw[3] << 8);

        var decompressed = Lzma1Decompress(raw[4..], declaredLength);
        Assert.Equal(declaredLength, decompressed.Length);

        var data = decompressed[4..];
        Assert.Equal(Crc32(data), BitConverter.ToUInt32(decompressed, 0));

        Assert.Equal(
            "\t1\t1\t5\tEUR\t20260731\t\t\t\tQR-ab29e346f1d841c8a95a63d857490818\tZmrzlina a káva" +
            "\t1\tSK4811000000002944116480\t\t0\t0\tKaviareň Mlynská dolina s.r.o.\t\t",
            System.Text.Encoding.UTF8.GetString(data));
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

    private static byte[] FromBase32Hex(string payload)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";
        var bits = 0;
        var buffer = 0;
        var bytes = new System.Collections.Generic.List<byte>();
        foreach (var c in payload)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }
        return [.. bytes];
    }

    private static byte[] Lzma1Decompress(byte[] compressed, int expectedLength)
    {
        var decoder = new SevenZip.Compression.LZMA.Decoder();
        // raw stream carries no props byte - reconstruct them from the spec
        // parameters: (pb * 5 + lp) * 9 + lc = (2 * 5 + 0) * 9 + 3 = 93,
        // then the 128 KiB dictionary size little-endian.
        decoder.SetDecoderProperties([93, 0x00, 0x00, 0x02, 0x00]);
        using var input = new System.IO.MemoryStream(compressed);
        using var output = new System.IO.MemoryStream();
        decoder.Code(input, output, compressed.Length, expectedLength, null);
        return output.ToArray();
    }

    [Fact]
    public void Sk_profile_resolves_builder_by_variant()
    {
        Assert.Equal("SK", SepaPaymentMethodHandler.ResolveQrBuilderKey(
            new SepaStoreSettings { CountryProfile = "SK", SkQrVariant = "payme" }));
        Assert.Equal(PayBySquarePayloadBuilder.ProfileKey, SepaPaymentMethodHandler.ResolveQrBuilderKey(
            new SepaStoreSettings { CountryProfile = "SK", SkQrVariant = "bysquare" }));
        // the variant is an SK-only concept - other profiles ignore it
        Assert.Equal("CZ", SepaPaymentMethodHandler.ResolveQrBuilderKey(
            new SepaStoreSettings { CountryProfile = "CZ", SkQrVariant = "bysquare" }));
        Assert.Equal("EU", SepaPaymentMethodHandler.ResolveQrBuilderKey(
            new SepaStoreSettings { CountryProfile = "EU", SkQrVariant = "payme" }));
    }
}
