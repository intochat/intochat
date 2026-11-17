using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Authority;

// os authority decides from the stamped Actor, never from an app id, kind or principal a request carried.
public sealed class StampedCallerAuthorityFacts
{
    private const string Brain = "b1";

    public enum Former { User, Agent, App, AppNamedPlatform, Scheduler, Platform, Module }

    // One row per former caller kind, as its Actor. An app keyed "platform" was the platform kind; it is now just an app.
    [Theory]
    [InlineData(Former.User, true, null)]
    [InlineData(Former.Agent, false, CallDenial.MissingGrant)]
    [InlineData(Former.App, false, CallDenial.MissingGrant)]
    [InlineData(Former.AppNamedPlatform, false, CallDenial.MissingGrant)]
    [InlineData(Former.Scheduler, true, null)]
    [InlineData(Former.Platform, true, null)]
    [InlineData(Former.Module, true, null)]
    public async Task GrantRulesDecideFromTheStampedActor(Former former, bool allowed, CallDenial? denial)
    {
        await using var os = TestOS.Local();
        var brain = new BrainId(Brain);
        var alice = new Actor.Person("alice", "alice");
        var caller = former switch
        {
            Former.User => os.Edge.Person("alice", "alice", brain),
            Former.Agent => os.Edge.Agent(alice, brain),
            Former.App => os.Edge.App("notes", alice, brain),
            Former.AppNamedPlatform => os.Edge.App("platform", alice, brain),
            Former.Scheduler => os.Edge.Scheduler(brain),
            Former.Platform => os.Edge.Kernel(brain),
            _ => os.Edge.Module("files", brain),
        };
        var member = new Member { PrincipalId = "alice", AccountId = "alice", BrainId = Brain, Role = MemberRole.Owner, DisplayName = "Alice" };
        var request = new CallRequest { TargetNeuron = $"mail/{Brain}/inbox", Operation = "Read" };

        var decision = GrantRules.Evaluate(caller, request, member, [], PrincipalSet.For(caller), connectionBound: false);

        Assert.NotNull(decision);
        Assert.Equal(allowed, decision.Allowed);
        if (denial is { } expected) { Assert.Equal(expected, decision.Denial); }
    }

    [Fact]
    public async Task APersonOutsideTheBrainIsRefusedWhateverItsPrincipalSays()
    {
        await using var os = TestOS.Local();
        var caller = os.Edge.Person("mallory", "mallory", new BrainId(Brain));
        var member = new Member { PrincipalId = "alice", AccountId = "alice", BrainId = Brain, Role = MemberRole.Owner, DisplayName = "Alice" };

        var decision = GrantRules.Evaluate(caller, new CallRequest { TargetNeuron = $"mail/{Brain}/inbox", Operation = "Read" }, member, [],
            PrincipalSet.For(caller), connectionBound: false);

        Assert.Equal(CallDenial.OutsideWorkspace, decision?.Denial);
    }

    // Review Focus 3: an app reaching the vault through the app proxy acts for its person, and only that person's secret
    // gets past the owner check (here to the unhosted vault, which answers NotFound); another person's is Denied.
    [Fact]
    public async Task AppProxyCallerResolvesOnlyItsPersonsSecret()
    {
        await using var os = TestOS.Local();
        var app = os.Edge.App("notes", new Actor.Person("alice", "alice"), new BrainId(Brain));
        var port = new VaultPort(services: null, edge: os.Edge, callers: new Ambient(app));

        var own = await port.Resolve(SecretReferences.For("alice", "token", "token", isSet: true), CancellationToken.None);
        var other = await port.Resolve(SecretReferences.For("bob", "token", "token", isSet: true), CancellationToken.None);

        Assert.Equal(SecretVaultStatus.NotFound, own.Status);
        Assert.Equal(SecretVaultStatus.Denied, other.Status);
    }

    [Fact]
    public async Task TheSchedulerResolvesNoPersonsSecret()
    {
        await using var os = TestOS.Local();
        var port = new VaultPort(services: null, edge: os.Edge, callers: new Ambient(os.Edge.Scheduler(new BrainId(Brain))));

        var result = await port.Resolve(SecretReferences.For("alice", "token", "token", isSet: true), CancellationToken.None);

        Assert.Equal(SecretVaultStatus.Denied, result.Status);
    }

    [Fact]
    public async Task SiloAccessFilterRefusesAnUnstampedCall()
    {
        var target = new NeuronId(new BrainId(Brain), "test.vault", "default");
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var context = new NeuronHostCall(GrainId.Create("neuron", target.ToString()),
            new RuntimeInvocation("test.vault", "IProbe", "default", "DoSomething", []));

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));

        Assert.Contains("carried no caller provenance", error.Message, StringComparison.Ordinal);
        Assert.False(context.Invoked);
    }

    private sealed class Ambient(Caller? current) : ICallerAmbient
    {
        public Caller? Current => current;
    }

    private sealed class AllowAll : ICallFilterStage
    {
        public ValueTask<CallDecision?> EvaluateAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
    }

    private sealed class NeuronHostCall : IIncomingGrainCallContext
    {
        private readonly Host _grain = new();

        public NeuronHostCall(GrainId targetId, RuntimeInvocation invocation)
        {
            TargetId = targetId;
            var proxy = DispatchProxy.Create<IInvokable, InvokableProxy>();
            ((InvokableProxy)(object)proxy).Invocation = invocation;
            Request = proxy;
        }

        public bool Invoked { get; private set; }
        public GrainId TargetId { get; }
        public GrainId? SourceId => null;
        public string InterfaceName => typeof(INeuronHost).FullName!;
        public string MethodName => nameof(INeuronHost.Invoke);
        public object Grain => _grain;
        public IInvokable Request { get; }
        public object? Result { get; set; }
        public Response? Response { get; set; }
        public MethodInfo InterfaceMethod => typeof(INeuronHost).GetMethod(nameof(INeuronHost.Invoke))!;
        public MethodInfo ImplementationMethod => InterfaceMethod;
        public IGrainContext TargetContext => null!;
        public GrainInterfaceType InterfaceType => default;

        public Task Invoke()
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Host : INeuronHost
    {
        public Task<bool> IsInterleaved(RuntimeInvocation call) => Task.FromResult(false);
        public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task Post(RuntimeInvocation call) => Task.CompletedTask;
        public Task PostInterleaved(RuntimeInvocation call) => Task.CompletedTask;

        public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<JsonElement?> InspectState(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
    }

    public class InvokableProxy : DispatchProxy
    {
        public RuntimeInvocation Invocation { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetArgumentCount" => 1,
            "GetArgument" => Invocation,
            _ => targetMethod?.ReturnType is { IsValueType: true } valueType && valueType != typeof(void)
                ? Activator.CreateInstance(valueType)
                : null,
        };
    }
}
