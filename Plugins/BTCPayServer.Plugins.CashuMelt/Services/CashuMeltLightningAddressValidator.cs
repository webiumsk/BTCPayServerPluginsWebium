#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public class CashuMeltLightningAddressValidator
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CashuMeltLightningAddressValidator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task ValidateForPayoutAsync(string lightningAddress, CancellationToken cancellationToken = default)
    {
        var resolver = new LightningAddressResolver(
            _httpClientFactory.CreateClient(nameof(LightningAddressResolver)));
        await CashuMeltSettingsValidation.ValidateLightningAddressResolvableAsync(
            lightningAddress, resolver, cancellationToken);
    }
}
