using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Pure-C# secp256k1 arithmetic and BDHKE (Blind Diffie-Hellman Key Exchange)
/// for the CashuMelt protocol (NUT-00).
/// https://github.com/cashubtc/nuts/blob/main/00.md
///
/// Uses BigInteger for field arithmetic – sufficient for the low operation count
/// per payment (a handful of point operations).
/// </summary>
public static class CashuMeltCrypto
{
    // ── secp256k1 curve parameters ──────────────────────────────────────────

    // Field prime p
    private static readonly BigInteger P =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F",
            System.Globalization.NumberStyles.HexNumber);

    // Curve order n
    private static readonly BigInteger N =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
            System.Globalization.NumberStyles.HexNumber);

    // Generator point G
    private static readonly AffinePoint G = new(
        BigInteger.Parse("079BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798",
            System.Globalization.NumberStyles.HexNumber),
        BigInteger.Parse("0483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8",
            System.Globalization.NumberStyles.HexNumber));

    // Exponent for sqrt in Fp (p ≡ 3 mod 4 → y = x^((p+1)/4))
    private static readonly BigInteger SqrtExp = (P + 1) / 4;

    private static readonly byte[] HashToCurveDomain =
        "Secp256k1_HashToCurve_Cashu_"u8.ToArray();

    // ── Public API ──────────────────────────────────────────────────────────

    /// NUT-00: hash_to_curve(message) – deterministic point from arbitrary bytes.
    private static AffinePoint HashToCurve(byte[] message)
    {
        // msg_to_hash = SHA256(domain || message)
        Span<byte> msgToHash = stackalloc byte[32];
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(HashToCurveDomain);
            sha.AppendData(message);
            sha.GetHashAndReset(msgToHash);
        }

        var pointBuf = new byte[33];
        pointBuf[0] = 0x02;
        var hash = new byte[32];
        var ctr  = new byte[4];

        for (uint counter = 0; ; counter++)
        {
            using var sha2 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha2.AppendData(msgToHash);
            BitConverter.TryWriteBytes(ctr.AsSpan(), counter);
            if (!BitConverter.IsLittleEndian) ctr.AsSpan().Reverse();
            sha2.AppendData(ctr);
            sha2.GetHashAndReset(hash.AsSpan());

            hash.CopyTo(pointBuf.AsSpan(1));

            var xBig = DecodeBigInt(pointBuf.AsSpan(1, 32));
            if (TryLiftX(xBig, prefix: 0x02, out var pt))
                return pt;
        }
    }

    /// <summary>
    /// NUT-00 step 2: B_ = hash_to_curve(secret) + r·G
    /// Returns the blinded point B_ (hex) and the scalar r (bytes).
    /// </summary>
    public static (string B_Hex, byte[] r) CreateBlindedMessage(byte[] secretBytes)
    {
        var Y = HashToCurve(secretBytes);

        // Generate a valid random scalar r (0 < r < n)
        byte[] rBytes;
        BigInteger r;
        do
        {
            rBytes = RandomNumberGenerator.GetBytes(32);
            r = new BigInteger(rBytes, isUnsigned: true, isBigEndian: true);
        } while (r == 0 || r >= N);

        var rG = ScalarMul(r, G);
        var B_ = PointAdd(Y, rG);

        return (EncodeCompressed(B_), rBytes);
    }

    /// <summary>
    /// NUT-00 step 4: C = C_ - r·K  (unblind the mint's signature)
    /// C_ and mintK are compressed-point hex strings.
    /// </summary>
    public static string UnblindSignature(string C_hex, string mintKeyHex, byte[] r)
    {
        var C_     = DecodeCompressed(C_hex);
        var K      = DecodeCompressed(mintKeyHex);
        var rScalar = new BigInteger(r, isUnsigned: true, isBigEndian: true);

        var rK    = ScalarMul(rScalar, K);
        var negRK = new AffinePoint(rK.x, Mod(P - rK.y));   // negate Y
        var C     = PointAdd(C_, negRK);

        return EncodeCompressed(C);
    }

    /// <summary>
    /// NUT-07: Y = hash_to_curve(secret) as compressed hex - identifies a proof
    /// for POST /v1/checkstate without revealing the unblinded signature.
    /// </summary>
    public static string ComputeYHex(byte[] secretBytes)
        => EncodeCompressed(HashToCurve(secretBytes));

    /// <summary>
    /// Decomposes an amount into its power-of-2 representation.
    /// E.g. 100 → [4, 32, 64]
    /// </summary>
    public static long[] DecomposeAmount(long amount)
    {
        if (amount <= 0) return Array.Empty<long>();
        var list = new List<long>(16);
        for (int bit = 0; bit < 63; bit++)
        {
            long denom = 1L << bit;
            if ((amount & denom) != 0) list.Add(denom);
        }
        return list.ToArray();
    }

    // ── secp256k1 point arithmetic ──────────────────────────────────────────

    private record struct AffinePoint(BigInteger x, BigInteger y)
    {
        public static readonly AffinePoint Infinity = new(BigInteger.Zero, BigInteger.Zero);
        public bool IsInfinity => x.IsZero && y.IsZero;
    }

    private static AffinePoint PointAdd(AffinePoint P, AffinePoint Q)
    {
        if (P.IsInfinity) return Q;
        if (Q.IsInfinity) return P;

        if (P.x == Q.x)
        {
            if (P.y != Q.y) return AffinePoint.Infinity; // P = -Q
            return PointDouble(P);
        }

        var lam = Mod((Q.y - P.y) * ModInverse(Q.x - P.x));
        var x3  = Mod(lam * lam - P.x - Q.x);
        var y3  = Mod(lam * (P.x - x3) - P.y);
        return new AffinePoint(x3, y3);
    }

    private static AffinePoint PointDouble(AffinePoint P)
    {
        if (P.IsInfinity) return P;
        // a = 0 for secp256k1, so λ = 3x² / 2y
        var lam = Mod(3 * P.x * P.x * ModInverse(2 * P.y));
        var x3  = Mod(lam * lam - 2 * P.x);
        var y3  = Mod(lam * (P.x - x3) - P.y);
        return new AffinePoint(x3, y3);
    }

    /// <summary>Double-and-add scalar multiplication.</summary>
    private static AffinePoint ScalarMul(BigInteger k, AffinePoint point)
    {
        k = ((k % N) + N) % N;
        var result  = AffinePoint.Infinity;
        var addend  = point;
        while (k > 0)
        {
            if (!k.IsEven) result = PointAdd(result, addend);
            addend = PointDouble(addend);
            k >>= 1;
        }
        return result;
    }

    // ── Field helpers ───────────────────────────────────────────────────────

    private static BigInteger Mod(BigInteger n) => ((n % P) + P) % P;

    private static BigInteger ModInverse(BigInteger n)
        => BigInteger.ModPow(Mod(n), P - 2, P);   // Fermat's little theorem

    // ── Point encoding/decoding ─────────────────────────────────────────────

    private static string EncodeCompressed(AffinePoint pt)
    {
        var buf = new byte[33];
        buf[0] = pt.y.IsEven ? (byte)0x02 : (byte)0x03;
        EncodeBigInt(pt.x, buf.AsSpan(1, 32));
        return Convert.ToHexString(buf).ToLower();
    }

    private static AffinePoint DecodeCompressed(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length != 33)
            throw new FormatException($"Compressed point must be 33 bytes, got {bytes.Length}");

        var xBig = DecodeBigInt(bytes.AsSpan(1, 32));
        if (!TryLiftX(xBig, bytes[0], out var pt))
            throw new FormatException("Invalid secp256k1 compressed point");
        return pt;
    }

    private static bool TryLiftX(BigInteger x, byte prefix, out AffinePoint pt)
    {
        pt = AffinePoint.Infinity;
        if (x <= 0 || x >= P) return false;

        // y² = x³ + 7 mod p
        var y2 = Mod(BigInteger.ModPow(x, 3, P) + 7);
        var y  = BigInteger.ModPow(y2, SqrtExp, P);

        // Verify y² == y2 (not all x values have a valid y)
        if (Mod(y * y) != y2) return false;

        // Choose the y parity matching the prefix byte
        bool wantEven = (prefix == 0x02);
        if (y.IsEven != wantEven) y = P - y;

        pt = new AffinePoint(x, y);
        return true;
    }

    private static BigInteger DecodeBigInt(ReadOnlySpan<byte> bytes)
        => new BigInteger(bytes, isUnsigned: true, isBigEndian: true);

    private static void EncodeBigInt(BigInteger value, Span<byte> dest)
    {
        dest.Clear();
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        // right-align into dest (pad with leading zeros)
        var src = bytes.AsSpan();
        src.CopyTo(dest[(dest.Length - src.Length)..]);
    }
}
