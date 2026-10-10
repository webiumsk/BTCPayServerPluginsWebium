using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.CashuMelt;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class HttpDestinationTests
{
    [Theory]
    [InlineData("https://mint.example.com/v1/quote", true)]
    [InlineData("http://127.0.0.1:8080/callback", false)]
    [InlineData("https://user:secret@mint.example.com/", false)]
    [InlineData("https://mint.example.com/#fragment", false)]
    [InlineData("file:///etc/passwd", false)]
    public void DestinationSyntax(string url, bool expected)
        => Assert.Equal(expected, HttpsDestinationHandler.IsAllowed(new Uri(url), null));

    [Fact]
    public void ExplicitHttpAuthorizationIsExact()
    {
        Assert.True(HttpsDestinationHandler.IsAllowed(new Uri("http://node.lan:8080/api"), "http://node.lan:8080"));
        Assert.False(HttpsDestinationHandler.IsAllowed(new Uri("http://other.lan:8080/api"), "http://node.lan:8080"));
        Assert.False(HttpsDestinationHandler.IsAllowed(new Uri("http://node.lan:8081/api"), "http://node.lan:8080"));
    }

    [Fact]
    public async Task ProtectedTransportRejectsLoopbackBeforeConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var services = new ServiceCollection();
        services.AddSingleton(new BTCPayServerOptions());
        services.AddTransient<HttpsDestinationHandler>();
        services.AddHttpClient("guarded", c => c.Timeout = TimeSpan.FromSeconds(3))
            .AddHttpMessageHandler<HttpsDestinationHandler>().UseSSRFProtection()
            .ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).AllowAutoRedirect = false);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("guarded");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{port}/callback"));
        Assert.False(listener.Pending());
    }
}
