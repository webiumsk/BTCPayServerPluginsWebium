#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

/// <summary>Shared behaviour of the two NOP transports (MQTT push, REST poll).</summary>
public abstract class NopSourceBase : IPaymentConfirmationSource
{
    protected readonly SepaConfigService ConfigService;
    protected readonly ILogger Logger;

    protected NopSourceBase(SepaConfigService configService, ILogger logger)
    {
        ConfigService = configService;
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract bool RequiresPolling { get; }

    public async Task<ConfirmationTestResult> TestAsync(SepaStoreSettings settings, CancellationToken cancellationToken)
    {
        var credentials = ConfigService.GetCredentials(settings);
        if (!credentials.HasNopCertificate)
            return new ConfirmationTestResult(false, "Upload the eKasa cash-register certificate first.");

        try
        {
            using var client = NopRestClient.Create(credentials, Logger);
            var status = await client.GetStatusAsync(cancellationToken);
            var identity = string.IsNullOrEmpty(settings.NopVatsk)
                ? ""
                : $" Identity: {settings.NopVatsk} / POKLADNICA-{settings.NopPokladnica}.";
            return new ConfirmationTestResult(true,
                $"NOP {credentials.NopEnvironment} reachable (instance {ReadInstance(status)}).{identity}");
        }
        catch (NopRestException ex)
        {
            return new ConfirmationTestResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            // TLS/certificate failures surface here - the message matters,
            // the certificate content must not.
            return new ConfirmationTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// SK references come from NOP - generateNewTransactionId. Failure falls
    /// back to the local NOP-shaped generator: the payment still goes
    /// through, but NOP will not route a notification for an id it did not
    /// issue, so confirmation degrades to Manual (documented in README).
    /// </summary>
    public async Task<string?> AcquireReferenceAsync(SepaStoreSettings settings, CancellationToken cancellationToken)
    {
        var credentials = ConfigService.GetCredentials(settings);
        if (!credentials.HasNopCertificate)
            return null;

        try
        {
            using var client = NopRestClient.Create(credentials, Logger);
            return await client.GenerateNewTransactionIdAsync(comment: null, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "NOP generateNewTransactionId failed for store {StoreId}; falling back to a local reference (confirmation degrades to manual)",
                settings.StoreId);
            return null;
        }
    }

    private static string ReadInstance(System.Text.Json.JsonElement status)
        => status.ValueKind == System.Text.Json.JsonValueKind.Object
           && status.TryGetProperty("instance", out var instance)
           && instance.ValueKind == System.Text.Json.JsonValueKind.String
            ? instance.GetString() ?? "?"
            : "?";
}

public class NopMqttSource : NopSourceBase
{
    public const string BackendId = "nop-mqtt";

    public NopMqttSource(SepaConfigService configService, ILogger<NopMqttSource> logger)
        : base(configService, logger)
    {
    }

    public override string Id => BackendId;
    public override bool RequiresPolling => false;
}

public class NopRestPollerSource : NopSourceBase
{
    public const string BackendId = "nop-rest";

    public NopRestPollerSource(SepaConfigService configService, ILogger<NopRestPollerSource> logger)
        : base(configService, logger)
    {
    }

    public override string Id => BackendId;
    public override bool RequiresPolling => true;
}
