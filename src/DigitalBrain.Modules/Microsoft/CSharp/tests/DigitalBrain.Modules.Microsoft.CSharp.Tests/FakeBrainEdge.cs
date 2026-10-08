using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Catalog;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// The brain side of a sandbox session, in process: reports are recorded, the stream carries whatever
// a test pushes, invokes answer with no content, and assemblies are served by hash.
internal sealed class FakeBrainEdge : HttpMessageHandler
{
    public const string Edge = "http://brain.test/brains/acc-brain/";
    public const string Store = "http://assemblies.test/";

    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
    public Channel<JsonNode> Reports { get; } = Channel.CreateUnbounded<JsonNode>();
    // Holds every report until released, as a brain busy with the very call that started the session would.
    public TaskCompletionSource Busy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldReports { get; init; }
    private readonly Channel<string> _events = Channel.CreateUnbounded<string>();

    public void Serve(CompiledBehavior behavior)
    {
        Blobs[behavior.Hash] = behavior.Assembly;
        Blobs[behavior.Hash + ".pdb"] = behavior.Symbols;
    }

    public AssignmentEvent Assignment(string behavior, CompiledBehavior compiled, Dictionary<string, string>? settings = null)
        => new(behavior, compiled.Hash, new Uri(Store + compiled.Hash), new Uri(Store + compiled.Hash + ".pdb"), settings ?? []);

    public void Push(string kind, object payload)
        => _events.Writer.TryWrite($"event: {kind}\ndata: {JsonSerializer.Serialize(payload, payload.GetType(), RuntimeProtocol.Json)}\n\n");

    // The next status report for a behavior, skipping heartbeats and other reports.
    public async Task<JsonNode> StatusAsync(string behavior, CancellationToken ct)
    {
        while (true)
        {
            var report = await Reports.Reader.ReadAsync(ct);
            if (report["status"] is { } status && status["behavior"]!.GetValue<string>() == behavior) { return status; }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "assemblies.test")
        {
            return Blobs.TryGetValue(uri.AbsolutePath.Trim('/'), out var bytes)
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new(HttpStatusCode.NotFound);
        }
        if (uri.AbsolutePath.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
        {
            var content = new StreamContent(new EventStream(_events.Reader));
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
        if (uri.AbsolutePath.EndsWith(RuntimeProtocol.Report, StringComparison.Ordinal))
        {
            if (HoldReports) { await Busy.Task.WaitAsync(cancellationToken); }
            Reports.Writer.TryWrite(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new(HttpStatusCode.OK);
        }
        return new(HttpStatusCode.NoContent);
    }

    // Reads pushed events as they arrive and stays open until the reader goes away.
    private sealed class EventStream(ChannelReader<string> events) : Stream
    {
        private ReadOnlyMemory<byte> _pending;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty) { _pending = Encoding.UTF8.GetBytes(await events.ReadAsync(cancellationToken)); }
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
