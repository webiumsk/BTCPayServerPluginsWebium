using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.TestFixtures;

/// <summary>Fresh, locally signed invoices for fake payment backends; no Lightning node or funds involved.</summary>
internal static class LightningInvoiceFixture
{
    private const string Alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    public static (string Bolt11, string Preimage) Create(long amountSats, TimeSpan? expiry = null)
    {
        var preimage = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var hrp = "lnbc" + checked(amountSats * 10) + "n";
        var data = new List<byte>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 6; i >= 0; i--) data.Add((byte)((timestamp >> (i * 5)) & 31));
        AddTag(data, 'p', ConvertBits(SHA256.HashData(preimage), 8, 5));
        AddTag(data, 'd', ConvertBits(Encoding.UTF8.GetBytes("Isolated payment test"), 8, 5));
        var lifetime = (long)(expiry ?? TimeSpan.FromHours(1)).TotalSeconds;
        var expiryWords = new List<byte>();
        do { expiryWords.Insert(0, (byte)(lifetime & 31)); lifetime >>= 5; } while (lifetime > 0);
        AddTag(data, 'x', expiryWords.ToArray());
        var signingHash = SHA256.HashData(Encoding.UTF8.GetBytes(hrp).Concat(ConvertBits(data.ToArray(), 5, 8)).ToArray());
        var keyBytes = new byte[32]; keyBytes[^1] = 1;
        using var key = new Key(keyBytes);
        var signature = key.SignCompact(new uint256(signingHash));
        data.AddRange(ConvertBits(signature.Signature.Concat(new[] { (byte)signature.RecoveryId }).ToArray(), 8, 5));
        return (new Bech32Encoder(Encoding.ASCII.GetBytes(hrp)).EncodeRaw(data.ToArray(), Bech32EncodingType.BECH32), Convert.ToHexString(preimage).ToLowerInvariant());
    }

    private static void AddTag(List<byte> data, char tag, byte[] words)
    {
        data.Add((byte)Alphabet.IndexOf(tag));
        data.Add((byte)(words.Length / 32)); data.Add((byte)(words.Length % 32)); data.AddRange(words);
    }

    private static byte[] ConvertBits(byte[] data, int from, int to)
    {
        var result = new List<byte>(); var accumulator = 0; var bits = 0;
        foreach (var value in data)
        {
            accumulator = (accumulator << from) | value; bits += from;
            while (bits >= to) { bits -= to; result.Add((byte)((accumulator >> bits) & ((1 << to) - 1))); }
        }
        if (bits > 0) result.Add((byte)((accumulator << (to - bits)) & ((1 << to) - 1)));
        return result.ToArray();
    }
}
