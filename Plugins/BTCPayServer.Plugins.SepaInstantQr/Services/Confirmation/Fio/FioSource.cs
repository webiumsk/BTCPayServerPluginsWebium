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

        // Fio deliberately delays the response ~30 s before rejecting an
        // invalid/inactive token (measured; documented as 500 = "nonexistent
        // or inactive token"). Cap the wait below upstream proxy timeouts and
        // translate the outcome, because the most common cause is completely
        // benign: a fresh token only becomes active ~5 minutes after its
        // authorization in internetbanking.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var document = await _client.GetTodayTransactionsAsync(credentials.FioToken!, timeout.Token);
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
        catch (OperationCanceledException)
        {
            return new ConfirmationTestResult(false,
                "Fio did not answer in time - this is how Fio reports an invalid or not-yet-active token. "
                + "A freshly generated token becomes active about 5 minutes after its authorization; "
                + "wait a few minutes and test again. Also check the token validity and scope in internetbanking.");
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            var message = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.InternalServerError =>
                    "Fio rejected the token (nonexistent or inactive). A fresh token becomes active ~5 minutes "
                    + "after authorization - wait and retry; otherwise re-check the token in internetbanking.",
                System.Net.HttpStatusCode.Conflict =>
                    "Fio rate limit: one request per token per 30 seconds - wait half a minute and test again.",
                System.Net.HttpStatusCode.UnprocessableEntity =>
                    "Fio refused the request (data older than 90 days need an unlock in internetbanking).",
                _ => $"Fio API test failed: {ex.Message}",
            };
            return new ConfirmationTestResult(false, message);
        }
        catch (Exception ex)
        {
            return new ConfirmationTestResult(false, $"Fio API test failed: {ex.Message}");
        }
    }
}
