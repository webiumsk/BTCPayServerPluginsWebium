using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;

/// <summary>
/// Always-available backend: the merchant checks their banking app and
/// presses "Mark as paid" (permission-gated) in the store UI. The actual
/// settle path goes through UISepaController → SepaMatchingService.
/// </summary>
public class ManualConfirmSource : IPaymentConfirmationSource
{
    public const string BackendId = "manual";

    public string Id => BackendId;

    public bool RequiresPolling => false;

    public Task<ConfirmationTestResult> TestAsync(SepaStoreSettings settings, CancellationToken cancellationToken)
        => Task.FromResult(new ConfirmationTestResult(true,
            "Manual confirmation has no external dependency - confirm payments from the store's SEPA page."));
}
