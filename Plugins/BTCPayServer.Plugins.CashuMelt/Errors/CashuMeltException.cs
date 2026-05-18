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
