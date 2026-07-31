using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class NopIdentityTests
{
    [Fact]
    public void Parses_the_manual_example_subject()
    {
        // Subject format from the NOP Lite integration manual.
        var identity = NopIdentity.FromSubject("C=SK, OU=88812345678900001, CN=VATSK-1234567890 POKLADNICA 88812345678900001");

        Assert.NotNull(identity);
        Assert.Equal("VATSK-1234567890", identity.Vatsk);
        Assert.Equal("88812345678900001", identity.Pokladnica);
        Assert.Equal("POKLADNICA-88812345678900001", identity.PokladnicaId);
    }

    [Fact]
    public void Parses_a_vrp_register_code()
    {
        // VRP registers use the 999 prefix instead of ORP's 888.
        var identity = NopIdentity.FromSubject("CN=VATSK-1122334455 POKLADNICA 99912345678900001, C=SK");

        Assert.NotNull(identity);
        Assert.Equal("99912345678900001", identity.Pokladnica);
    }

    [Theory]
    [InlineData("CN=example.com, O=Some Company")]
    [InlineData("CN=VATSK-1234567890")]
    [InlineData("")]
    public void Rejects_non_ekasa_subjects(string subject)
    {
        Assert.Null(NopIdentity.FromSubject(subject));
    }

    [Fact]
    public void Loads_identity_from_a_generated_pfx_certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "C=SK, CN=VATSK-1234567890 POKLADNICA 88812345678900001",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var credentials = new SepaBackendCredentials
        {
            NopPfxBase64 = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "secret")),
            NopPfxPassword = "secret",
        };

        using var loaded = NopCertificateLoader.Load(credentials);
        var identity = NopIdentity.FromCertificate(loaded);

        Assert.NotNull(identity);
        Assert.Equal("VATSK-1234567890", identity.Vatsk);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Loads_identity_from_a_pem_pair()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=VATSK-9876543210 POKLADNICA 88800000000000001, C=SK",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var credentials = new SepaBackendCredentials
        {
            NopCertificatePem = certificate.ExportCertificatePem(),
            NopPrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
        };

        using var loaded = NopCertificateLoader.Load(credentials);
        Assert.Equal("VATSK-9876543210", NopIdentity.FromCertificate(loaded)!.Vatsk);
        Assert.True(loaded.HasPrivateKey);
    }
}
