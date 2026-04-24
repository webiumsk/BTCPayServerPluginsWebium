#nullable enable
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Cashu has no public chain explorer for ecash transfers; we register a provider so BTCPay UI stays consistent.
/// </summary>
public sealed class CashuMeltTransactionLinkProvider : TransactionLinkProvider
{
    public override string? OverrideBlockExplorerLink { get; set; }
    public override string? BlockExplorerLinkDefault => null;

    public override string? GetTransactionLink(string paymentId) => null;
}
