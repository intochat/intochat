using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Runs the shipped scan and real script client. Only the HTTP edge is controlled, so a transient
// gateway timeout and a competing status write can land at exact boundaries without slowing a host.
public sealed class InoScanRecoveryFacts
{
    private const string Legacy = "Could not answer a message Memory was slow to intend: The brain refused IntoChat.OS.Modules.Flutter.Text.IText.Read (504): Gateway timeout";
    private const string Recovering = "Could not check pending messages; retrying: ";
    private const string Failed = "Could not check pending messages; retrying.";
    private static readonly Lazy<byte[]> Script = new(Compile);
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(Legacy)]
    [InlineData(Recovering + "The brain refused IntoChat.OS.Modules.Flutter.Text.IText.Read (504): Gateway timeout")]
    public async Task AHealthyScanClearsItsPersistedFailureAfterRestart(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new ScanEdge();
        edge.WriteStatus(failure);
        await using var running = await Running.Start(edge, ct);

        await TestWait.UntilAsync(_ => Task.FromResult(edge.Status), status => status.Markdown.Length == 0, Within, ct);
    }

    [Fact]
    public async Task ATransientReadFailureIsRetriedAndItsNoticeClearsAfterRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new ScanEdge { FailReads = true };
        await using var running = await Running.Start(edge, ct);
        var failed = await TestWait.UntilAsync(_ => Task.FromResult(edge.Status), status => status.Markdown == Failed, Within, ct);

        // Generic copy: the gateway's error text stays in the server log.
        Assert.Equal(Failed, failed.Markdown);
        Assert.DoesNotContain("504", failed.Markdown, StringComparison.Ordinal);
        edge.FailReads = false;
        await TestWait.UntilAsync(_ => Task.FromResult(edge.Status), status => status.Markdown.Length == 0, Within, ct);
    }

    [Theory]
    [InlineData("A newer warning")]
    [InlineData("Memory was slow to intend; answering without a brief")]
    [InlineData("Memory could not intend; answering without a brief")]
    public async Task AHealthyScanPreservesStatusesItDoesNotOwn(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new ScanEdge();
        edge.WriteStatus(status);
        await using var running = await Running.Start(edge, ct);
        await TestWait.UntilAsync(_ => Task.FromResult(edge.Scans), scans => scans >= 2, Within, ct);

        Assert.Equal(status, edge.Status.Markdown);
        Assert.Equal(1, edge.Status.Version);
    }

    [Fact]
    public async Task RecoveryPreservesAStatusWrittenAfterItsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new ScanEdge { ReplaceBeforeClear = "A newer warning" };
        edge.WriteStatus(Legacy);
        await using var running = await Running.Start(edge, ct);
        await edge.Competed.Task.WaitAsync(Within, ct);
        await TestWait.UntilAsync(_ => Task.FromResult(edge.Scans), scans => scans >= 2, Within, ct);

        Assert.Equal("A newer warning", edge.Status.Markdown);
        Assert.Equal(2, edge.Status.Version);
    }

    private static byte[] Compile()
    {
        var feature = new FeatureDirectory(SolutionProjects.Features).Load().Single(item => item.Owner == "os" && item.Bundle.Name == "ino");
        var sources = feature.Bundle.Files.Where(file => AppFiles.IsProgram(file.Key)
            && (file.Key == "intended.cs" || CSharpScriptCheck.IsShared(file.Value)))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var compiled = new CSharpScriptCheck().Compile(sources);
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
        return compiled.Assemblies["intended.cs"].Assembly;
    }

    private sealed class Running(SessionConnection session, CancellationTokenSource stop, Task execution) : IAsyncDisposable
    {
        public static async Task<Running> Start(ScanEdge edge, CancellationToken ct)
        {
            var session = await SessionConnection.OpenAsync(new("http://brain.test/brains/scan-brain/"), "fixture-token", "fixture-fingerprint", edge, ct);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var ambient = AmbientSession.Enter(session, ScanEdge.App + "/intended.cs", new Dictionary<string, string>
            { ["App"] = ScanEdge.App, ["Brain"] = "scan-brain" }, stop.Token);
            var entry = Assembly.Load(Script.Value).EntryPoint!;
            var execution = Task.Run(async () =>
            {
                try
                {
                    if (entry.Invoke(null, [Array.Empty<string>()]) is Task task) { await task; }
                }
                catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException && stop.IsCancellationRequested) { }
            }, CancellationToken.None);
            return new(session, stop, execution);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await execution; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await session.DisposeAsync();
            stop.Dispose();
        }
    }

    private sealed class ScanEdge : HttpMessageHandler
    {
        public const string App = "scan-brain/apps/ino";
        private readonly Lock _gate = new();
        private TextState _status = new() { Name = App + "/status" };
        private int _scans;
        public bool FailReads { get; set; }
        public string? ReplaceBeforeClear { get; init; }
        public TaskCompletionSource Competed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Scans => Volatile.Read(ref _scans);
        public TextState Status { get { lock (_gate) { return new() { Name = _status.Name, Version = _status.Version, Markdown = _status.Markdown }; } } }

        public void WriteStatus(string text)
        {
            lock (_gate) { _status = new() { Name = _status.Name, Markdown = text, Version = _status.Version + 1 }; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The stream wait must be cancelled.");
            }
            if (path.EndsWith(RuntimeProtocol.Report, StringComparison.Ordinal))
            {
                var report = await request.Content!.ReadFromJsonAsync<RuntimeReport>(RuntimeProtocol.Json, cancellationToken);
                return report?.Subscribe is not null ? Json(new { subscription = "intents" }) : new(HttpStatusCode.OK);
            }
            var call = (await request.Content!.ReadFromJsonAsync<RuntimeInvocation>(RuntimeProtocol.Json, cancellationToken))!;
            if (call.Contract == typeof(IApp).FullName && call.Method == nameof(IApp.Read))
            {
                if (call.Key == App) { Interlocked.Increment(ref _scans); }
                return Json(new AppSnapshot(call.Key, new("", "", [], [], []), "", [], [], [], "1.0.0", AppStatus.Live, [], []));
            }
            if (call.Contract == typeof(IText).FullName)
            {
                if (call.Method == nameof(IText.Read))
                {
                    if (call.Key.EndsWith("/pending/index-routed", StringComparison.Ordinal) && FailReads)
                    { return new(HttpStatusCode.GatewayTimeout) { Content = new StringContent("Gateway timeout") }; }
                    return Json(call.Key == App + "/status" ? Status : new TextState { Name = call.Key });
                }
                if (call.Key == App + "/status")
                {
                    var text = call.Arguments[0].GetString()!;
                    if (call.Method == nameof(IText.Set)) { WriteStatus(text); return new(HttpStatusCode.NoContent); }
                    if (call.Method == "TrySet")
                    {
                        lock (_gate)
                        {
                            if (ReplaceBeforeClear is { } replacement && !Competed.Task.IsCompleted)
                            { WriteStatus(replacement); Competed.TrySetResult(); }
                            if (call.Arguments[1].GetInt32() != _status.Version) { return Json(false); }
                            WriteStatus(text);
                            return Json(true);
                        }
                    }
                }
            }
            throw new InvalidOperationException($"Unexpected script call: {call.Contract}.{call.Method} ({call.Key}).");
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RuntimeProtocol.Json) };
    }
}
