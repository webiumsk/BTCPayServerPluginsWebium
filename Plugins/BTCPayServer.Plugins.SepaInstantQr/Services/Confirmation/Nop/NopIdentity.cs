#nullable enable
using System;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using BTCPayServer.Plugins.SepaInstantQr.Services;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

/// <summary>
/// The NOP client identity derived from the eKasa cash-register certificate
/// subject: CN = "VATSK-XXXXXXXXXX POKLADNICA XXXXXXXXXXXXXXXX". VATSK and
/// POKLADNICA drive REST authorization and the MQTT topic ACLs.
/// </summary>
public sealed partial record NopIdentity(string Vatsk, string Pokladnica)
{
    [GeneratedRegex(@"CN\s*=\s*(VATSK-[0-9]+)\s+POKLADNICA\s+([0-9A-Za-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectPattern();

    /// <summary>"VATSK-1234567890" and "POKLADNICA-88812345678900001".</summary>
    public string PokladnicaId => $"POKLADNICA-{Pokladnica}";

    public static NopIdentity? FromSubject(string subject)
    {
        var match = SubjectPattern().Match(subject);
        if (!match.Success)
            return null;

        return new NopIdentity(match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value);
    }

    public static NopIdentity? FromCertificate(X509Certificate2 certificate)
        => FromSubject(certificate.Subject);
}

/// <summary>Loads the client certificate from the stored credentials.</summary>
public static class NopCertificateLoader
{
    public static X509Certificate2 Load(SepaBackendCredentials credentials)
    {
        if (!string.IsNullOrEmpty(credentials.NopPfxBase64))
        {
            var bytes = Convert.FromBase64String(credentials.NopPfxBase64);
            return X509CertificateLoader.LoadPkcs12(
                bytes,
                string.IsNullOrEmpty(credentials.NopPfxPassword) ? null : credentials.NopPfxPassword,
                X509KeyStorageFlags.Exportable);
        }

        if (!string.IsNullOrEmpty(credentials.NopCertificatePem) && !string.IsNullOrEmpty(credentials.NopPrivateKeyPem))
        {
            var pem = X509Certificate2.CreateFromPem(credentials.NopCertificatePem, credentials.NopPrivateKeyPem);
            // Re-import through PKCS#12 so the private key is usable for TLS
            // client auth on every platform (ephemeral-key limitation).
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }

        throw new InvalidOperationException("No NOP client certificate is configured for this store.");
    }
}
