using System.Net;
using System.Text;
using DigitalBrain.Client.Edge.V1;

namespace DigitalBrain.Client.Tests.Unit;

public sealed class EdgeSignalFacts
{
    public sealed record Changed(int Value) : Signal;
    public interface ISource : INeuron { Task Read(); }

    [Fact]
    public async Task OverflowIsVisibleWithoutDrainingTheBuffer()
    {
        var events = string.Concat(Enumerable.Range(0, 1100).Select(i => $"event: {ScriptEdgeProtocol.SignalEvent}\ndata: {{\"value\":{i}}}\n\n"));
        using var http = new HttpClient(new Handler(events)) { BaseAddress = new("https://example.test") };
        var edge = new ScriptEdgeClient(http);
        var source = (NeuronProxy)(object)NeuronProxy.Create<ISource>(edge, "source");
        await using var subscription = await EdgeSignalSubscription<Changed>.OpenAsync(edge, source, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Contains("overflow", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentDisposalIsSafe()
    {
        using var http = new HttpClient(new Handler("")) { BaseAddress = new("https://example.test") };
        var edge = new ScriptEdgeClient(http);
        var source = (NeuronProxy)(object)NeuronProxy.Create<ISource>(edge, "source");
        var subscription = await EdgeSignalSubscription<Changed>.OpenAsync(edge, source, TestContext.Current.CancellationToken);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => subscription.DisposeAsync().AsTask()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.Completion);
    }

    [Fact]
    public async Task BreakingOutOfOnClosesTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = string.Concat(Enumerable.Range(0, 3).Select(i => $"event: {ScriptEdgeProtocol.SignalEvent}\ndata: {{\"value\":{i}}}\n\n"));
        var handler = new Handler(events);
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test") };
        var connection = DigitalBrainConnection.ForTests(http);
        var source = connection.Get<ISource>("source");
        await foreach (var signal in connection.On<Changed>(source, ct)) { Assert.Equal(0, signal.Value); break; }
        Assert.True(handler.Disposed, "the SSE response must be disposed when the consumer breaks out");
    }

    private sealed class Handler(string events) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new Observed(Encoding.UTF8.GetBytes(events), () => Disposed = true));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        private sealed class Observed(byte[] bytes, Action onDispose) : MemoryStream(bytes)
        {
            protected override void Dispose(bool disposing) { onDispose(); base.Dispose(disposing); }
        }
    }
}
