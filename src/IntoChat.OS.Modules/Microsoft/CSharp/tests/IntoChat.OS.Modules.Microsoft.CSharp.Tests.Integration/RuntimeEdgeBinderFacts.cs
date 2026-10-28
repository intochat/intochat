using System.Text.Json;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Modules.Microsoft.CSharp.Edge;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using IntoChat.OS.Modules.Time;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
using HostInvocation = DigitalBrain.Invocation.RuntimeInvocation;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// The runtime edge reaches a plain contract through the silo host only after its own access checks. A call the edge
// refuses never invokes the neuron; an allowed one invokes it once.
public sealed class RuntimeEdgeBinderFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ACallTheEdgeRefusesNeverReachesTheBinder()
    {
        var ct = TestContext.Current.CancellationToken;
        var binder = new RecordingCalls();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<TimeModule>()
            .ConfigureServices(services => services.AddSingleton<IOutgoingGrainCallFilter>(binder)).StartAsync(ct);
        var token = await Token(brain, ct);
        binder.Armed = true;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Edge(brain).InvokeAsync("acc-brain", token, Start("another-brain/tea"), ct));

        Assert.Empty(binder.Starts);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAllowedCallIsBoundOnceAndAnswersAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var binder = new RecordingCalls();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<TimeModule>()
            .ConfigureServices(services => services.AddSingleton<IOutgoingGrainCallFilter>(binder)).StartAsync(ct);
        var token = await Token(brain, ct);
        binder.Armed = true;

        var answer = await Edge(brain).InvokeAsync("acc-brain", token, Start("acc-brain/tea"), ct);

        Assert.Null(answer);
        var asked = Assert.Single(binder.Starts);
        Assert.EndsWith("/tea", asked, StringComparison.Ordinal);
    }

    private static RuntimeInvocation Start(string key)
        => new("acc-brain/csharp/a", typeof(ITimer).FullName!, key, nameof(ITimer.Start),
            [JsonSerializer.SerializeToElement(TimeSpan.FromMinutes(5), RuntimeProtocol.Json), JsonSerializer.SerializeToElement<TimeSpan?>(null, RuntimeProtocol.Json)]);

    private static RuntimeEdge Edge(ITestBrain brain) => ActivatorUtilities.CreateInstance<RuntimeEdge>(brain.Services);

    private static async Task<string> Token(ITestBrain brain, CancellationToken ct)
    {
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        return brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", session);
    }

    private sealed class RecordingCalls : IOutgoingGrainCallFilter
    {
        public bool Armed;
        public List<string> Starts { get; } = [];

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (Armed && context.InterfaceMethod.Name is "Invoke" or "InvokeInterleaved"
                && context.Request.GetArgument(0) is HostInvocation { Method: nameof(ITimer.Start) } call)
            { Starts.Add(call.Key); }
            return context.Invoke();
        }
    }
}
