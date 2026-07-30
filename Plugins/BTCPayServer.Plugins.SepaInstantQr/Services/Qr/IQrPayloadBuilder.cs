namespace BTCPayServer.Plugins.SepaInstantQr.Services.Qr;

public interface IQrPayloadBuilder
{
    /// <summary>Country profile the builder serves: SK | CZ | EU.</summary>
    string Profile { get; }

    string Build(SepaQrRequest request);
}
