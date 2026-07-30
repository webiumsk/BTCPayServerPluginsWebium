#nullable enable
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Bank transfers have no public explorer. NOP-shaped references (QR-...)
/// can at least link to the public transaction-history diagnostics site.
/// </summary>
public sealed class SepaTransactionLinkProvider : TransactionLinkProvider
{
    public override string? OverrideBlockExplorerLink { get; set; }
    public override string? BlockExplorerLinkDefault => null;

    public override string? GetTransactionLink(string paymentId)
        => paymentId.StartsWith("QR-", System.StringComparison.OrdinalIgnoreCase)
            ? $"https://www.kdejemojaplatba.sk/?id={paymentId}"
            : null;
}
