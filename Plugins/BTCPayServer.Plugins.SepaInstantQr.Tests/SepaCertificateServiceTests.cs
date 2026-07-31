using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

/// <summary>
/// The shared certificate service behind both the settings UI and the
/// Greenfield API. Uses an ephemeral data protector - no DB access happens
/// in these paths.
/// </summary>
public class SepaCertificateServiceTests
{
    private static (SepaCertificateService Service, SepaConfigService Config) CreateServices()
    {
        var config = new SepaConfigService(
            new SepaDbContextFactory(Options.Create(new DatabaseOptions())),
            new EphemeralDataProtectionProvider());
        return (new SepaCertificateService(config), config);
    }

    private static X509Certificate2 CreateEkasaCertificate(
        RSA rsa,
        string subject = "C=SK, CN=VATSK-1234567890 POKLADNICA 88812345678900001",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void Applies_a_pfx_upload_and_caches_the_identity()
    {
        var (service, config) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            "secret", null, null, "INT"));

        Assert.Null(error);
        Assert.Equal("VATSK-1234567890", settings.NopVatsk);
        Assert.Equal("88812345678900001", settings.NopPokladnica);
        var credentials = config.GetCredentials(settings);
        Assert.True(credentials.HasNopCertificate);
        Assert.Equal("INT", credentials.NopEnvironment);
    }

    [Fact]
    public void Applies_a_pem_pair()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            null, null, certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem(), "PROD"));

        Assert.Null(error);
        Assert.Equal("VATSK-1234567890", settings.NopVatsk);
    }

    [Fact]
    public void Rejects_half_a_pem_pair()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            null, null, certificate.ExportCertificatePem(), null, "INT"));

        Assert.Contains("together with its private key", error);
        Assert.Null(settings.NopVatsk);
    }

    [Fact]
    public void Rejects_pfx_and_pem_together()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            "secret", certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem(), "INT"));

        Assert.Contains("not both", error);
    }

    [Fact]
    public void Rejects_invalid_base64()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };

        var error = service.Apply(settings, new SepaCertificateUpload(
            "not-valid-base64!!!", null, null, null, "INT"));

        Assert.Contains("base64", error);
    }

    [Fact]
    public void Rejects_a_certificate_without_private_key()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Cert)),
            null, null, null, "INT"));

        Assert.NotNull(error);
        Assert.Null(settings.NopVatsk);
    }

    [Fact]
    public void Rejects_an_expired_certificate()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa,
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "x")),
            "x", null, null, "INT"));

        Assert.Contains("expired", error);
    }

    [Fact]
    public void Rejects_a_not_yet_valid_certificate()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa,
            notBefore: DateTimeOffset.UtcNow.AddDays(2),
            notAfter: DateTimeOffset.UtcNow.AddYears(1));

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "x")),
            "x", null, null, "INT"));

        Assert.Contains("not valid yet", error);
    }

    [Fact]
    public void Rejects_a_non_ekasa_subject()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa, subject: "CN=example.com, O=Regular Web Server");

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "x")),
            "x", null, null, "INT"));

        Assert.Contains("eKasa", error);
    }

    [Fact]
    public void Rejects_a_pfx_with_a_wrong_password()
    {
        var (service, _) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);

        var error = service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            "wrong", null, null, "INT"));

        Assert.NotNull(error);
        Assert.Null(settings.NopVatsk);
    }

    [Fact]
    public void Environment_only_apply_keeps_the_stored_certificate()
    {
        var (service, config) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);
        Assert.Null(service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            "secret", null, null, "INT")));

        var error = service.Apply(settings, new SepaCertificateUpload(null, null, null, null, "PROD"));

        Assert.Null(error);
        Assert.Equal("VATSK-1234567890", settings.NopVatsk);
        var credentials = config.GetCredentials(settings);
        Assert.True(credentials.HasNopCertificate);
        Assert.Equal("PROD", credentials.NopEnvironment);
    }

    [Fact]
    public void Clear_removes_material_and_identity()
    {
        var (service, config) = CreateServices();
        var settings = new SepaStoreSettings { StoreId = "store" };
        using var rsa = RSA.Create(2048);
        using var certificate = CreateEkasaCertificate(rsa);
        Assert.Null(service.Apply(settings, new SepaCertificateUpload(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            "secret", null, null, "INT")));

        service.Clear(settings, "INT");

        Assert.Null(settings.NopVatsk);
        Assert.Null(settings.NopPokladnica);
        Assert.False(config.GetCredentials(settings).HasNopCertificate);
    }
}
