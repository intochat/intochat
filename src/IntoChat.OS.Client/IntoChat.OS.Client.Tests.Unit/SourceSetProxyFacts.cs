using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Signals.Watch;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using static IntoChat.OS.Client.Tests.Unit.SessionConnectionFacts;

namespace IntoChat.OS.Client.Tests.Unit;

// A set over the script's own neurons: its members are NeuronProxy instances, not grain references.
public sealed class SourceSetProxyFacts
{
    [Fact]
    public async Task ASetOverTheScriptsProxiesYieldsEachMembersSignalOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SetBrain();
        await using var session = await SessionConnection.OpenAsync(new Uri("https://brain.test/brains/acc-brain/"), "token-1", ContractsFingerprint.Of([typeof(Neuron).Assembly]), brain, ct);
        await using var connection = await Connect(session, ct);
        var sources = Sources.Of(connection.Get<ISource>("set-proxy-a"), connection.Get<ISource>("set-proxy-a"), connection.Get<ISource>("set-proxy-b"));

        var seen = new List<int>();
        await foreach (var signal in connection.On<Changed>(sources, ct))
        {
            seen.Add(signal.Value);
            if (seen.Count == 2) { break; }
        }

        Assert.Equal([1, 2], seen.Order());
        Assert.Equal(["set-proxy-a", "set-proxy-b"], brain.Subscribed.Order(StringComparer.Ordinal));
    }

    private static async Task<IntoChatOSConnection> Connect(SessionConnection session, CancellationToken ct)
    {
        using (AmbientSession.Enter(session, "acc-brain/csharp/a", new Dictionary<string, string>(), ct))
        { return await IntoChatOSClient.ConnectAsync([], ct); }
    }

    // Answers each subscribe with a subscription named after its neuron, and streams one signal per
    // member: set-proxy-a publishes 1, set-proxy-b publishes 2. The stream then stays open.
    private sealed class SetBrain : HttpMessageHandler
    {
        public ConcurrentQueue<string> Subscribed { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            {
                var pipe = new Pipe();
                var events = $"event: {ReadyEvent.Kind}\ndata: {{\"session\":\"s\"}}\n\n" + Batch("sub-set-proxy-a", 1) + Batch("sub-set-proxy-b", 2);
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(events), cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } } };
            }
            if (body?["subscribe"] is { } subscribe)
            {
                var key = subscribe["key"]!.GetValue<string>();
                Subscribed.Enqueue(key);
                return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"subscription\":\"sub-{key}\"}}") };
            }
            return new(HttpStatusCode.OK);
        }

        private static string Batch(string subscription, int value)
            => $"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(new SignalBatchEvent(subscription, [JsonSerializer.Serialize(new { value })], 1, null), RuntimeProtocol.Json)}\n\n";
    }
}
