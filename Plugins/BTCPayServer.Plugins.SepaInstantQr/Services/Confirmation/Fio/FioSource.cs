#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;

/// <summary>
/// Fio banka confirmation backend: token-based (no certificates), driven by
/// <see cref="SepaPollingHostedService"/>. Payment references stay locally
/// generated - the payer's bank carries them as the SEPA end-to-end id
/// (PayMe PI / bysquare ORI) or the variable symbol (CZ), and Fio exposes
/// them back via column27/column5.
/// </summary>
public class FioSource : IPaymentConfirmationSource
{
    public const string BackendId = "fio";

    private readonly FioApiClient _client;
    private readonly SepaConfigService _configService;

    public FioSource(FioApiClient client, SepaConfigService configService)
    {
        _client = client;
        _configService = configService;
    }

    public string Id => BackendId;

    public bool RequiresPolling => true;

    public async Task<ConfirmationTestResult> TestAsync(SepaStoreSettings settings, CancellationToken cancellationToken)
    {
        var credentials = _configService.GetCredentials(settings);
        if (!credentials.HasFioToken)
            return new ConfirmationTestResult(false, "No Fio API token stored - save one first.");

        try
        {
            using var document = await _client.GetTodayTransactionsAsync(credentials.FioToken!, cancellationToken);
            var info = document.RootElement.GetProperty("accountStatement").GetProperty("info");
            var iban = info.TryGetProperty("iban", out var ibanValue) ? ibanValue.GetString() : null;
            var currency = info.TryGetProperty("currency", out var currencyValue) ? currencyValue.GetString() : null;
            return new ConfirmationTestResult(true,
                $"Fio API OK - account {iban ?? "?"} ({currency ?? "?"}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConfirmationTestResult(false, $"Fio API test failed: {ex.Message}");
        }
    }
}
