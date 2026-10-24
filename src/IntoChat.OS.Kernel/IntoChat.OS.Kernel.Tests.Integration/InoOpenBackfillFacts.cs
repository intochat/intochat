using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using IntoChat.OS.Apps;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Runs the shipped open.cs on the real script client. The chat index back-fill is best effort: an index that
// fails every call must not keep Ino from composing and presenting its surface.
public sealed class InoOpenBackfillFacts
{
    private const string App = "open-brain/apps/ino";

    [Fact]
    public async Task AThrowingIndexStillLetsInoComposeAndPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new OpenEdge();
        var session = await SessionConnection.OpenAsync(new("http://brain.test/brains/open-brain/"), "fixture-token", "fixture-fingerprint", edge, ct);
        await using var _ = session;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task execution;
        using (AmbientSession.Enter(session, App + "/open.cs", new Dictionary<string, string>
               { ["App"] = App, ["Brain"] = "open-brain", ["ProgramRevision"] = "1" }, stop.Token))
        {
            var entry = Assembly.Load(Compile()).EntryPoint!;
            execution = Task.Run(async () =>
            {
                try { if (entry.Invoke(null, [Array.Empty<string>()]) is Task task) { await task; } }
                catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException && stop.IsCancellationRequested) { }
            }, CancellationToken.None);
        }

        await edge.Presented.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await TestWait.UntilAsync(_ => Task.FromResult(edge.IndexCalls), calls => calls > 0, TimeSpan.FromSeconds(15), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        Assert.False(execution.IsFaulted, execution.Exception?.ToString());
        Assert.DoesNotContain(edge.Calls, call => call.EndsWith(nameof(IApp.FailPresentation), StringComparison.Ordinal));
        await stop.CancelAsync();
        try { await execution; } catch (OperationCanceledException) { }
    }

    // Every Ino script compiles against the shared files, including the sidebar's chats.cs and search.cs.
    [Fact]
    public void EveryInoProgramCompiles()
    {
        var feature = new FeatureDirectory(SolutionProjects.Features).Load().Single(item => item.Owner == "os" && item.Bundle.Name == "ino");
        var sources = feature.Bundle.Files.Where(file => AppFiles.IsProgram(file.Key))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var compiled = new CSharpScriptCheck().Compile(sources);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Errors));
        Assert.Contains("chats.cs", compiled.Assemblies.Keys);
        Assert.Contains("search.cs", compiled.Assemblies.Keys);
        Assert.Contains("view.cs", compiled.Assemblies.Keys);
        Assert.Contains("index.cs", compiled.Assemblies.Keys);
    }

    private static byte[] Compile()
    {
        var feature = new FeatureDirectory(SolutionProjects.Features).Load().Single(item => item.Owner == "os" && item.Bundle.Name == "ino");
        var sources = feature.Bundle.Files.Where(file => AppFiles.IsProgram(file.Key)
            && (file.Key == "open.cs" || CSharpScriptCheck.IsShared(file.Value)))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var compiled = new CSharpScriptCheck().Compile(sources);
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
        return compiled.Assemblies["open.cs"].Assembly;
    }

    private sealed class OpenEdge : HttpMessageHandler
    {
        private int _indexCalls;
        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource Presented { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IndexCalls => Volatile.Read(ref _indexCalls);

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
                return report?.Subscribe is not null ? Json(new { subscription = "open" }) : new(HttpStatusCode.OK);
            }
            var call = (await request.Content!.ReadFromJsonAsync<RuntimeInvocation>(RuntimeProtocol.Json, cancellationToken))!;
            Calls.Enqueue(call.Contract + "." + call.Method);
            if (call.Contract == typeof(IChatIndex).FullName)
            {
                Interlocked.Increment(ref _indexCalls);
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("index down") };
            }
            if (call.Contract == typeof(IChat).FullName && call.Method == nameof(IChat.Read))
            { return Json(new ChatState { Name = call.Key, Selection = new(App + "/conversations/main", 1) }); }
            if (call.Contract == typeof(IText).FullName && call.Method == nameof(IText.Read)) { return Json(new TextState { Name = call.Key }); }
            if (call.Contract == typeof(ITextField).FullName && call.Method == nameof(ITextField.Read)) { return Json(new TextFieldState { Name = call.Key }); }
            if (call.Contract == typeof(IApp).FullName && call.Method == nameof(IApp.Present)) { Presented.TrySetResult(); }
            return Json<object?>(null);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RuntimeProtocol.Json) };
    }
}
