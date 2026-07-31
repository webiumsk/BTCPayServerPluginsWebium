#nullable enable
using System.Text.Json;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Backend credentials stored as a data-protected JSON blob in
/// <see cref="SepaStoreSettings.EncryptedCredentialsJson"/>. Never logged;
/// views only ever see *_set booleans.
/// </summary>
public record SepaBackendCredentials
{
    // ── NOP (Slovak instant-payment notifications, eKasa certificate) ──
    /// <summary>PEM certificate (with the private key in NopPrivateKeyPem).</summary>
    public string? NopCertificatePem { get; init; }

    public string? NopPrivateKeyPem { get; init; }

    /// <summary>PKCS#12 bundle (base64) - alternative to the PEM pair.</summary>
    public string? NopPfxBase64 { get; init; }

    public string? NopPfxPassword { get; init; }

    /// <summary>"INT" | "PROD".</summary>
    public string NopEnvironment { get; init; } = "INT";

    public bool HasNopCertificate =>
        (!string.IsNullOrEmpty(NopCertificatePem) && !string.IsNullOrEmpty(NopPrivateKeyPem))
        || !string.IsNullOrEmpty(NopPfxBase64);

    public static SepaBackendCredentials FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new SepaBackendCredentials();

        try
        {
            return JsonSerializer.Deserialize<SepaBackendCredentials>(json) ?? new SepaBackendCredentials();
        }
        catch (JsonException)
        {
            return new SepaBackendCredentials();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
