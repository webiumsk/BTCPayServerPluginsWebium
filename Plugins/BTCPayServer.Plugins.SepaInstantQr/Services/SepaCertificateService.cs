#nullable enable
using System;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Certificate material for the NOP backend - either a PKCS#12 blob or a
/// PEM pair. Values are applied to the encrypted credentials store and are
/// never logged or echoed back.
/// </summary>
public record SepaCertificateUpload(
    string? PfxBase64,
    string? PfxPassword,
    string? CertPem,
    string? KeyPem,
    string NopEnvironment);

/// <summary>
/// Shared NOP eKasa certificate handling for the settings UI and the
/// Greenfield API: validates the upload, caches the parsed VATSK/POKLADNICA
/// identity on the settings row and stores the material encrypted.
/// </summary>
public class SepaCertificateService
{
    private readonly SepaConfigService _configService;

    public SepaCertificateService(SepaConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>Removes the stored certificate and identity, keeping the environment.</summary>
    public void Clear(SepaStoreSettings settings, string nopEnvironment)
    {
        var credentials = _configService.GetCredentials(settings);
        _configService.ApplyCredentials(settings, credentials with
        {
            NopCertificatePem = null,
            NopPrivateKeyPem = null,
            NopPfxBase64 = null,
            NopPfxPassword = null,
            NopEnvironment = nopEnvironment,
        });
        settings.NopVatsk = null;
        settings.NopPokladnica = null;
    }

    /// <summary>
    /// Applies the upload (or just the environment change when no material
    /// is supplied). Returns null on success, otherwise a human-readable
    /// error - the caller decides how to surface it (ModelState vs 400).
    /// </summary>
    public string? Apply(SepaStoreSettings settings, SepaCertificateUpload upload)
    {
        var credentials = _configService.GetCredentials(settings);
        var updated = credentials with { NopEnvironment = upload.NopEnvironment };
        var uploaded = false;

        var hasPfx = !string.IsNullOrWhiteSpace(upload.PfxBase64);
        var hasCertPem = !string.IsNullOrWhiteSpace(upload.CertPem);
        var hasKeyPem = !string.IsNullOrWhiteSpace(upload.KeyPem);

        if (hasPfx && (hasCertPem || hasKeyPem))
            return "Upload either the PEM pair or the PKCS#12 file, not both.";
        if (hasCertPem != hasKeyPem)
            return "Upload the PEM certificate together with its private key.";

        if (hasPfx)
        {
            try
            {
                Convert.FromBase64String(upload.PfxBase64!);
            }
            catch (FormatException)
            {
                return "The PKCS#12 payload is not valid base64.";
            }

            updated = updated with
            {
                NopPfxBase64 = upload.PfxBase64,
                NopPfxPassword = upload.PfxPassword,
                NopCertificatePem = null,
                NopPrivateKeyPem = null,
            };
            uploaded = true;
        }
        else if (hasCertPem)
        {
            updated = updated with
            {
                NopCertificatePem = upload.CertPem,
                NopPrivateKeyPem = upload.KeyPem,
                NopPfxBase64 = null,
                NopPfxPassword = null,
            };
            uploaded = true;
        }

        if (uploaded)
        {
            try
            {
                using var certificate = NopCertificateLoader.Load(updated);
                if (!certificate.HasPrivateKey)
                    return "The certificate has no private key - mTLS authentication needs it (upload the key file or a complete .p12).";

                // NotBefore/NotAfter are local-time DateTimes - convert
                // before comparing against UtcNow.
                if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                    return $"The certificate expired on {certificate.NotAfter:yyyy-MM-dd}.";

                if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                    return $"The certificate is not valid yet (valid from {certificate.NotBefore:yyyy-MM-dd}).";

                var identity = NopIdentity.FromCertificate(certificate);
                if (identity is null)
                    return "The certificate subject does not look like an eKasa cash-register certificate (expected CN \"VATSK-... POKLADNICA ...\").";

                settings.NopVatsk = identity.Vatsk;
                settings.NopPokladnica = identity.Pokladnica;
            }
            catch (Exception ex)
            {
                return $"Could not load the certificate: {ex.Message}";
            }
        }

        _configService.ApplyCredentials(settings, updated);
        return null;
    }
}
