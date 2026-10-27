using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using IntoChat.OS.Modules.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// 06-07 (R4-15): a compiled feature script calls a plain neuron (the migrated Time timer) and hears its [Signal] records with
// the same brain.Get / brain.On it uses for grains, over the host's real HTTP runtime edge and the brain runtime's session synapse.
public sealed class PlainScriptFacts
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

    [Trait("host", "own")]
    [Fact]
    public async Task AScriptStartsAPlainTimerAndHearsItsTick()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<TimeModule>().StartAsync(ct);
        var compilation = brain.Services.GetRequiredService<CSharpScriptCheck>().Compile(new Dictionary<string, string>
        {
            ["tick.cs"] = """
                using IntoChat.OS.Modules.Time.Timers.Signals;
                using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
                await using var brain = await IntoChatOSClient.ConnectAsync(args);
                var timer = brain.Get<ITimer>("acc-brain/tea");
                await foreach (var tick in brain.On<TimerTick>(timer, () => timer.Start(TimeSpan.FromMilliseconds(50)), brain.Stopping))
                {
                    Console.WriteLine("tick:" + tick.TimerId);
                    break;
                }
                """,
        });
        Assert.True(compilation.Success, string.Join("; ", compilation.Errors));
        var compiled = compilation.Assemblies["tick.cs"];
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        var token = brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", session);
        var blobs = new FakeBrainEdge();
        blobs.Serve(compiled);
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"),
            token, RuntimeBrain.Fingerprint(brain), new ForwardRuntime(brain.HttpClient), ct);
        await using var run = await BehaviorLoaderFacts.Loader(blobs).LoadAsync(connection,
            blobs.Assignment("acc-brain/csharp/a", compiled), NullLogger.Instance, ct);

        Assert.True(await run.Completion.WaitAsync(Within, ct) == 0, run.Error + "\n" + run.Tail(20));
        Assert.Contains("tick:acc-brain/tea", run.Tail(10), StringComparison.Ordinal);
        var subscription = Assert.Single((await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).Behaviors.SelectMany(behavior => behavior.Subscriptions));
        Assert.Equal("plain:acc-brain/timer/tea", subscription.Neuron);
        Assert.Equal("time.timer-tick", subscription.Signal);
    }

    private sealed class ForwardRuntime(HttpClient brain) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return brain.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }
}
