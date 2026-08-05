using System;

namespace BTCPayServer.Plugins.CashuMelt.Errors;

/// <summary>Base for all CashuMelt exceptions.</summary>
public abstract class CashuMeltException : Exception
{
    protected CashuMeltException(string message) : base(message) { }
    protected CashuMeltException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>User-facing error: message is safe to show to the customer at checkout.</summary>
public class CashuMeltUserException : CashuMeltException
{
    public CashuMeltUserException(string message) : base(message) { }
}

/// <summary>System-level error: do not expose message to the customer; log and surface a generic message.</summary>
public class CashuMeltSystemException : CashuMeltException
{
    public CashuMeltSystemException(string message) : base(message) { }
    public CashuMeltSystemException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Structured mint protocol error (NUT-00 error body {"detail": ..., "code": ...}).
/// Derives from HttpRequestException so call sites that treat HTTP failures as transient
/// keep doing so for codes they do not explicitly handle.
/// </summary>
public class CashuMeltMintProtocolException : System.Net.Http.HttpRequestException
{
    /// <summary>NUT-00 error code: the proofs were already redeemed at the mint.</summary>
    public const int TokenAlreadySpent = 11001;

    public int MintErrorCode { get; }
    public string? Detail { get; }

    public CashuMeltMintProtocolException(int mintErrorCode, string? detail, System.Net.HttpStatusCode statusCode)
        : base($"Mint error {mintErrorCode}: {detail}", null, statusCode)
    {
        MintErrorCode = mintErrorCode;
        Detail = detail;
    }
}
