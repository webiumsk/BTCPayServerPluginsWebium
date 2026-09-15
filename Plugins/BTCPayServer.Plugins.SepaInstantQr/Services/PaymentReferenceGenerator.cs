using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Locally generated payment references.
///
/// SK/EU: "QR-" + 32 lowercase hex - the same shape NOP's
/// generateNewTransactionId produces (UUIDv4 without dashes), so switching a
/// store to a NOP backend later changes nothing downstream.
/// CZ: numeric variable symbol, 10 digits, no leading zero (bank VS field).
/// </summary>
public static partial class PaymentReferenceGenerator
{
    public static string NewEndToEndId()
        => "QR-" + Guid.NewGuid().ToString("N");

    public static string NewVariableSymbol()
    {
        // First digit 1-9, remaining nine digits 0-9 → always 10 digits,
        // ~9 * 10^9 space; uniqueness is additionally enforced by the
        // primary key on SepaPaymentRequest.Reference.
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt64(bytes) % 9_000_000_000UL;
        return (1_000_000_000UL + value).ToString();
    }

    /// <summary>
    /// True for a NOP-shaped transaction id (QR- + 32 hex, either case) -
    /// the only shape the public NOP diagnostics endpoint accepts. Whether
    /// NOP actually issued the id is a different question (local ids look
    /// identical).
    /// </summary>
    public static bool IsNopShaped(string? reference)
        => reference is not null && NopShapedId().IsMatch(reference);

    [GeneratedRegex("^QR-[0-9a-fA-F]{32}$")]
    private static partial Regex NopShapedId();
}
