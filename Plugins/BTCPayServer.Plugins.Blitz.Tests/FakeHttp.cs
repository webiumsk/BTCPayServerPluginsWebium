using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Blitz.Tests;

/// <summary>Deterministic HttpMessageHandler mapping exact URL -> (status, body) for unit tests.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public readonly Dictionary<string, (HttpStatusCode Code, string Body)> Routes = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Requests = new();

    public FakeHttp Map(string url, string body, HttpStatusCode code = HttpStatusCode.OK)
    { Routes[url] = (code, body); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        if (!Routes.TryGetValue(url, out var r))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        return Task.FromResult(new HttpResponseMessage(r.Code) { Content = new StringContent(r.Body) });
    }

    public HttpClient Client() => new(this);
}

sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly FakeHttp _fake;
    public FakeHttpClientFactory(FakeHttp fake) => _fake = fake;
    public HttpClient CreateClient(string name) => _fake.Client();
}
