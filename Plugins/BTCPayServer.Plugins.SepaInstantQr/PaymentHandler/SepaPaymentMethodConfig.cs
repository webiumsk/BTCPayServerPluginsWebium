namespace BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;

/// <summary>
/// Minimal config stored in the store's payment method blob. The full
/// configuration (IBAN, profile, backend) lives in the SepaStoreSettings
/// table.
/// </summary>
public class SepaPaymentMethodConfig
{
    public bool Enabled { get; set; } = true;
}
