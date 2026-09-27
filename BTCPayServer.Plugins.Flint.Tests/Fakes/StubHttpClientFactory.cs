using System.Net;
using System.Reflection;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// An <see cref="IHttpClientFactory"/> that hands out one client over a handler a test controls.
/// </summary>
/// <remarks>
/// The alternative - letting the catalogue reach the real endpoint - would make the suite depend on a third
/// party's uptime to answer a question about this plugin's own caching, and would make "the fetch failed" a
/// scenario nobody could arrange on purpose.
/// </remarks>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;

    public StubHttpClientFactory(StubHttpMessageHandler handler)
    {
        _handler = handler;
    }

    /// <summary>The names the catalogue asked for, so a test can assert it used its own named client.</summary>
    public List<string> Requested { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Requested.Add(name);
        return new HttpClient(_handler, disposeHandler: false);
    }
}

/// <summary>
/// A handler that answers with whatever a test decided, and counts what it was asked.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _requests;

    private StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    /// <summary>How many requests reached this handler. The whole point of the fan-out tests.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>The URLs asked for, in order.</summary>
    public List<string> Urls { get; } = [];

    /// <summary>Completes when the first request arrives, so a test need not poll for it.</summary>
    public Task Started => _started.Task;

    /// <summary>
    /// Routes every request through <paramref name="respond"/>, letting a test inspect the request and
    /// decide the response. Use when the default factory methods do not expose enough control.
    /// </summary>
    public static StubHttpMessageHandler Capture(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(respond);

    /// <summary>Answers every request with <paramref name="body"/> as <c>200 application/json</c>.</summary>
    public static StubHttpMessageHandler Returning(string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));

    /// <summary>Answers every request with <paramref name="status"/> and no useful body.</summary>
    public static StubHttpMessageHandler Failing(HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>Refuses to connect at all, which is what an air-gapped or firewalled server looks like.</summary>
    public static StubHttpMessageHandler Offline() =>
        new((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("no route to host")));

    /// <summary>
    /// Returns responses from <paramref name="statuses"/> in order, then repeats the last one for any
    /// subsequent requests. Useful for testing retry logic: pass e.g. 503, 503, 200 to get two failures
    /// followed by a success.
    /// </summary>
    public static StubHttpMessageHandler Sequence(params HttpStatusCode[] statuses)
    {
        var index = 0;
        return new StubHttpMessageHandler((_, _) =>
        {
            var i = Math.Min(Interlocked.Increment(ref index) - 1, statuses.Length - 1);
            return Task.FromResult(new HttpResponseMessage(statuses[i]));
        });
    }

    /// <summary>
    /// Fails the first request with a network exception, then returns 200 for every request after.
    /// The inverse of <see cref="OnceThenOffline"/>: useful for testing retry logic where the
    /// first attempt hits a transient network error and the second one succeeds.
    /// </summary>
    public static StubHttpMessageHandler FailOnceThenOK()
    {
        var called = 0;
        return new StubHttpMessageHandler((_, _) => Interlocked.Increment(ref called) == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
    }

    /// <summary>Answers the first request and refuses every one after it: an endpoint that goes away.</summary>
    public static StubHttpMessageHandler OnceThenOffline(string body)
    {
        var served = 0;

        return new StubHttpMessageHandler((_, _) => Interlocked.Increment(ref served) == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            })
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    }

    /// <summary>
    /// Answers with a body that starts like the real one and then never stops.
    /// </summary>
    /// <remarks>
    /// Chunked, so there is no <c>Content-Length</c> to check - which is why the ceiling has to be applied while
    /// reading rather than off a header. This is the shape of the failure worth defending against: not a large
    /// response, but one that has no end.
    /// </remarks>
    public static StubHttpMessageHandler Endless() =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream())
        }));

    /// <summary>
    /// Holds every request open until the returned gate is released.
    /// </summary>
    /// <remarks>
    /// This is how the "one fetch however many renders" test is made deterministic rather than timing-based: the
    /// first fetch is still in flight while the other renders happen, so a second request would have to be a
    /// second request and not merely a fast one.
    /// </remarks>
    public static StubHttpMessageHandler Blocking(string body, out TaskCompletionSource release)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = gate;

        return new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);

        lock (Urls)
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);

        _started.TrySetResult();

        return _respond(request, cancellationToken);
    }
}

/// <summary>
/// A response body that opens a route table and then emits whitespace for ever.
/// </summary>
/// <remarks>
/// Whitespace rather than junk on purpose: junk would fail the parse, which is a different failure. This one is
/// valid JSON as far as it goes and simply never ends, so the only thing that can stop it is the byte ceiling.
/// </remarks>
public sealed class EndlessStream : Stream
{
    private static readonly byte[] Opening = "{\"routes\":["u8.ToArray();

    private int _opened;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_opened < Opening.Length)
        {
            var take = Math.Min(count, Opening.Length - _opened);
            Array.Copy(Opening, _opened, buffer, offset, take);
            _opened += take;
            return take;
        }

        Array.Fill(buffer, (byte)' ', offset, count);
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Payloads recorded from the provider, embedded in this assembly.
/// </summary>
public static class RecordedPayloads
{
    /// <summary>
    /// <c>GET https://orchestration.flashnet.xyz/v1/orchestration/routes</c>, recorded 2026-08-07.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every route object is verbatim from the live response, field for field. It is a <em>subset</em>: all 114
    /// routes whose source chain is Spark - which is everything the projection can possibly keep - plus twenty
    /// sourced elsewhere, so the source filter has something to filter. Eight of those twenty are the tokenised
    /// equities that reach Robinhood Chain from other sources and never from Spark; they are in the recording
    /// on purpose, because they are what an unfiltered projection would put in a merchant's sweep picker. The
    /// real body carries 2,851 routes and about 1.9 MB.
    /// </para>
    /// <para>
    /// Recorded rather than hand-written because a hand-written payload tests the parser against the author's
    /// idea of the format. This one carries the things nobody would have thought to invent: chain ids as
    /// strings, CAIP-2 namespaces on the non-EVM chains, a numeric chain id on Tron whose addresses are base58,
    /// a 34-character "contract address" on HyperCore, a <c>USD₮0</c> symbol with a non-ASCII character in it,
    /// and USDT at 18 decimals on BSC where it is 6 everywhere else.
    /// </para>
    /// </remarks>
    public static string OrchestrationRoutes { get; } = Read("orchestration-routes.json");

    private static string Read(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly
            .GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
