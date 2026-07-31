using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

internal static class PeriodicTimerExtensions
{
    /// <summary>
    /// WaitForNextTickAsync that reports cancellation as `false` instead of
    /// throwing - shared by the plugin's hosted-service loops.
    /// </summary>
    public static async Task<bool> WaitNextTickSafeAsync(this PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
