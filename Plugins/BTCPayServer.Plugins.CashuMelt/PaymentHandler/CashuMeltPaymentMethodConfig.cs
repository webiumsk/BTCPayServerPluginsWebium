using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.CashuMelt.PaymentHandler;

/// <summary>
/// Minimal config stored in store's payment method blob.
/// Full config (MintUrl, etc.) is in CashuMeltStoreSettings table.
/// </summary>
public class CashuMeltPaymentMethodConfig
{
    public bool Enabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsConfigured => true; // We check DB for full config
}
