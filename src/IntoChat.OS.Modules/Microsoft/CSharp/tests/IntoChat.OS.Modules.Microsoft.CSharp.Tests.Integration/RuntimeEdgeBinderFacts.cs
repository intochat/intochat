using System.Text.Json;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Modules.Microsoft.CSharp.Edge;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using IntoChat.OS.Modules.Time;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// 06-06b (T-06-20): the runtime edge reaches a plain contract through the DigitalBrain proxy and binder (IPlainNeurons) only
// after its own access checks. A call the edge refuses never asks for a proxy; an allowed one does, once.
public sealed class RuntimeEdgeBinderFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ACallTheEdgeRefusesNeverReachesTheBinder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<TimeModule>().StartAsync(ct);
        var (edge, binder) = Edge(brain);
        var token = await Token(brain, ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edge.InvokeAsync("acc-brain", token, Start("another-brain/tea"), ct));

        Assert.Empty(binder.Asked);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAllowedCallIsBoundOnceAndAnswersAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<TimeModule>().StartAsync(ct);
        var (edge, binder) = Edge(brain);
        var token = await Token(brain, ct);

        var answer = await edge.InvokeAsync("acc-brain", token, Start("acc-brain/tea"), ct);

        Assert.Null(answer);
        var asked = Assert.Single(binder.Asked);
        Assert.Equal((typeof(ITimer), "acc-brain/tea"), asked);
    }

    private static RuntimeInvocation Start(string key)
        => new("acc-brain/csharp/a", typeof(ITimer).FullName!, key, nameof(ITimer.Start),
            [JsonSerializer.SerializeToElement(TimeSpan.FromMinutes(5), RuntimeProtocol.Json), JsonSerializer.SerializeToElement<TimeSpan?>(null, RuntimeProtocol.Json)]);

    private static (RuntimeEdge Edge, RecordingBinder Binder) Edge(ITestBrain brain)
    {
        var binder = new RecordingBinder(brain.Services.GetRequiredService<IPlainNeurons>());
        return (ActivatorUtilities.CreateInstance<RuntimeEdge>(brain.Services, binder), binder);
    }

    private static async Task<string> Token(ITestBrain brain, CancellationToken ct)
    {
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        return brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", session);
    }

    private sealed class RecordingBinder(IPlainNeurons inner) : IPlainNeurons
    {
        public System.Collections.Concurrent.ConcurrentQueue<(Type Contract, string Key)> Asked { get; } = new();

        public object Get(Type contract, string fullKey)
        {
            Asked.Enqueue((contract, fullKey));
            return inner.Get(contract, fullKey);
        }
    }
}
