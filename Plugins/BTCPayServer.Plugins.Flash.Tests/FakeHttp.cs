using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flash.Tests;

/// <summary>Deterministic HttpMessageHandler mapping exact URL -> (status, body) for unit tests.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public readonly Dictionary<string, (HttpStatusCode Code, string Body)> Routes = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private readonly List<string> _requests = new();

    private TaskCompletionSource? _hold;
    private int _waiting;

    /// <summary>Snapshot of recorded request URLs, in arrival order (thread-safe).</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    /// <summary>Requests currently parked behind <see cref="HoldResponses"/>.</summary>
    public int WaitingCount => Volatile.Read(ref _waiting);

    public FakeHttp Map(string url, string body, HttpStatusCode code = HttpStatusCode.OK)
    { Routes[url] = (code, body); return this; }

    /// <summary>
    /// Opt-in asynchronous gate: subsequent requests record themselves, then wait until
    /// <see cref="ReleaseResponses"/> - lets concurrency tests prove work does not complete
    /// synchronously. Call before issuing requests.
    /// </summary>
    public FakeHttp HoldResponses()
    {
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    public void ReleaseResponses() => _hold?.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (_lock) _requests.Add(url);
        if (_hold is { } hold)
        {
            Interlocked.Increment(ref _waiting);
            try { await hold.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        if (!Routes.TryGetValue(url, out var r))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        return new HttpResponseMessage(r.Code) { Content = new StringContent(r.Body) };
    }

    // disposeHandler: false - the handler is shared between clients; disposing one
    // client must not tear down the fake for the others.
    public HttpClient Client() => new(this, disposeHandler: false);
}

/// <summary>All-404 in-memory factory - never creates a network-capable client.</summary>
sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new FakeHttp().Client();
}

sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly FakeHttp _fake;
    public FakeHttpClientFactory(FakeHttp fake) => _fake = fake;
    public HttpClient CreateClient(string name) => _fake.Client();
}
