using System;
using System.Security.Cryptography;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Locally generated payment references.
///
/// SK/EU: "QR-" + 32 lowercase hex - the same shape NOP's
/// generateNewTransactionId produces (UUIDv4 without dashes), so switching a
/// store to a NOP backend later changes nothing downstream.
/// CZ: numeric variable symbol, 10 digits, no leading zero (bank VS field).
/// </summary>
public static class PaymentReferenceGenerator
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
}
