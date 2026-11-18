using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Screens;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

public interface IStubSecurityContract
{
    Task DoSomething();
}

public sealed class EdgeSecurityBoundaryFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public EdgeSecurityBoundaryFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T2_Edge_ExternalCaller_ClaimingKernelActor_ThrowsUnauthorizedAccessException()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;
        var targetNeuron = GrainId.Create("neuron", "test-brain/auth/default");

        var wire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(wire, (byte)5); // ActorKernel = 5
        wireType.GetField("Brain")!.SetValue(wire, "test-brain");

        RequestContext.Set("db.access.caller", wire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.Contains("External client cannot claim actor kind 5", ex.Message);
            Assert.False(executed);
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
    }

    [Fact]
    public async Task T2_Edge_ExternalCaller_ClaimingSchedulerActor_ThrowsUnauthorizedAccessException()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;
        var targetNeuron = GrainId.Create("neuron", "test-brain/auth/default");

        var wire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(wire, (byte)4); // ActorScheduler = 4
        wireType.GetField("Brain")!.SetValue(wire, "test-brain");

        RequestContext.Set("db.access.caller", wire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.Contains("External client cannot claim actor kind 4", ex.Message);
            Assert.False(executed);
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
    }

    [Fact]
    public async Task T2_Edge_ExternalCaller_ClaimingModuleActor_ThrowsUnauthorizedAccessException()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;
        var targetNeuron = GrainId.Create("neuron", "test-brain/auth/default");

        var wire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(wire, (byte)3); // ActorModule = 3
        wireType.GetField("Brain")!.SetValue(wire, "test-brain");

        RequestContext.Set("db.access.caller", wire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.Contains("External client cannot claim actor kind 3", ex.Message);
            Assert.False(executed);
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
    }

    [Fact]
    public async Task T2_Edge_ExternalCaller_ClaimingInvalidActorByte_ThrowsUnauthorizedAccessException()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;
        var targetNeuron = GrainId.Create("neuron", "test-brain/auth/default");

        var wire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(wire, (byte)255); // Invalid byte
        wireType.GetField("Brain")!.SetValue(wire, "test-brain");

        RequestContext.Set("db.access.caller", wire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.Contains("External client cannot claim actor kind 255", ex.Message);
            Assert.False(executed);
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
    }

    [Fact]
    public async Task T2_Edge_ExternalCaller_TamperedInsideAndVia_StrippedCleanlyToFalseAndNull()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;
        var targetNeuron = GrainId.Create("neuron", "test-brain/auth/default");

        var wire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(wire, (byte)0); // ActorPerson = 0
        wireType.GetField("Principal")!.SetValue(wire, "alice");
        wireType.GetField("Account")!.SetValue(wire, "acct_1");
        wireType.GetField("Brain")!.SetValue(wire, "test-brain");
        wireType.GetField("Inside")!.SetValue(wire, true); // Forgery attempt
        wireType.GetField("Via")!.SetValue(wire, "victim/proxy/0"); // Forgery attempt

        RequestContext.Set("db.access.caller", wire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                var inside = (bool)wireType.GetField("Inside")!.GetValue(wire)!;
                var via = wireType.GetField("Via")!.GetValue(wire);
                Assert.False(inside);
                Assert.Null(via);
                return Task.CompletedTask;
            });

            await filter.Invoke(context);
            Assert.True(executed);
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
    }

    [Fact]
    public void T2_Edge_EnterInProcess_ActiveScope_ElevatesTurnToInternal_AndDisposalReverts()
    {
        var edge = _fixture.Edge;

        // Baseline: inactive
        Assert.False(edge.IsKernelActive);

        // Enter scope: elevated
        using (edge.EnterInProcess())
        {
            Assert.True(edge.IsKernelActive);
        }

        // Disposal: cleanly reverted
        Assert.False(edge.IsKernelActive);
    }

    [Fact]
    public async Task T2_AccessGate_UnauthenticatedCallCarryingNoCaller_ThrowsUnauthorizedAccessException()
    {
        var brain = NewBrain("edge-unauth");
        var targetNeuron = new NeuronId(brain, "test.vault", "default");

        // When no ambient caller is stamped, SiloAccessCallFilter rejects the neuron call
        var filter = new SiloAccessCallFilter([], new StubCallerAmbient(null));
        var invocation = new RuntimeInvocation("test.vault", nameof(IStubSecurityContract), "default", "DoSomething", []);
        var context = new UnstampedNeuronHostCallContext(GrainId.Create("neuron", targetNeuron.ToString()), invocation);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));

        Assert.Contains("carried no caller provenance", ex.Message);
    }

    [Fact]
    public void T2_AccessPolicy_CrossBrainCall_DeniesWithOutsideBrain_AndGateThrows()
    {
        var edge = _fixture.Edge;
        var brainA = new BrainId("brain-alpha");
        var brainB = new BrainId("brain-beta");
        var targetNeuronA = new NeuronId(brainA, "test.vault", "default");

        var callerB = edge.Person("bob", "acct_2", brainB);
        var decision = AccessPolicy.Decide(callerB, targetNeuronA, OperationAccess.Owner);

        Assert.False(decision.Allowed);
        Assert.Equal(AccessDenial.OutsideBrain, decision.Denial);
        Assert.Contains("may not call a neuron of brain", decision.Explanation);
        Assert.Equal(AccessDenial.OutsideBrain, AccessPolicy.Decide(callerB, targetNeuronA, OperationAccess.Internal).Denial);
    }

    [Fact]
    public void T2_AccessPolicy_ExternalClientCallingInternalOperation_DeniesWithInternalOnly_AndGateThrows()
    {
        var edge = _fixture.Edge;
        var brain = new BrainId("brain-internal-fence");
        var targetNeuron = new NeuronId(brain, "test.kernel_admin", "default");

        var externalCaller = edge.Person("alice", "acct_1", brain);
        Assert.False(externalCaller.Inside);

        var decision = AccessPolicy.Decide(externalCaller, targetNeuron, OperationAccess.Internal);
        Assert.False(decision.Allowed);
        Assert.Equal(AccessDenial.InternalOnly, decision.Denial);
        Assert.Contains("reachable only from inside the cluster", decision.Explanation);
    }

    [Fact]
    public async Task T2_Screen_PayloadSanitizer_UntrustedByField_StrippedAndOverwrittenWithHostAssertedBy()
    {
        var os = _fixture.OS;
        os.Register<PayloadSanitizerProbeScreen>();

        var brain = NewBrain("payload-sanitizer");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.payload_sanitizer_probe", "default");
        var screen = os.Get<IScreen>(screenId);

        // Client-supplied "by" in Request args is stripped (becomes null) rather than trusting attacker claim
        var spoofedArgs = JsonSerializer.Deserialize<JsonElement>("{\"by\":\"attacker@evil.com\",\"action\":\"deposit\",\"amount\":500}");
        await screen.Request("payload_sanitizer.deposit", spoofedArgs);
        await os.Idle();

        var afterRequest = await screen.Read();
        Assert.NotNull(afterRequest);
        Assert.Contains("By: none;Action: deposit;Amount: 500", afterRequest.View.GetRawText());

        // Host Activate stamps host-asserted "by" onto emitted signal payload
        await screen.Activate("app.payload_sanitizer_probe", afterRequest.Generation, "btn-deposit", fieldValue: null, by: "alice@intochat.io");
        await os.Idle();

        var afterActivate = await screen.Read();
        Assert.NotNull(afterActivate);
        Assert.Contains("By: alice@intochat.io;Action: deposit;Amount: 500", afterActivate.View.GetRawText());
    }

    public sealed record PayloadProbeState(string? LastBy = null, string LastAction = "", int LastAmount = 0)
    {
        public PayloadProbeState() : this(null, "", 0) { }
    }

    [Signal("payload_sanitizer.deposit")]
    public sealed record PayloadProbeDeposit(
        string Action,
        int Amount,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

    /*
    # PayloadSanitizerProbe
    Screen verifying host-reserved "by" claim sanitization.
    */
    [Neuron("app.payload_sanitizer_probe")]
    public sealed class PayloadSanitizerProbeScreen : Screen<PayloadProbeState>
    {
        protected override View Render(PayloadProbeState state) => new(
            "PayloadProbe",
            new Column([
                new Text($"By: {state.LastBy ?? "none"};Action: {state.LastAction};Amount: {state.LastAmount}"),
                new Button("btn-deposit", "Deposit", Emits(new PayloadProbeDeposit("deposit", 500))),
            ]));

        [On]
        public Task OnDeposit(PayloadProbeDeposit signal) => Show(new PayloadProbeState(signal.By, signal.Action, signal.Amount));
    }

    private sealed class StubNeuronHost : INeuronHost
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

    private class InvokableProxy : DispatchProxy
    {
        public RuntimeInvocation Invocation { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetArgumentCount" => 1,
            "GetArgument" => Invocation,
            _ => targetMethod?.ReturnType is { IsValueType: true } vt && vt != typeof(void)
                ? Activator.CreateInstance(vt)
                : null,
        };
    }

    private sealed class UnstampedNeuronHostCallContext : IIncomingGrainCallContext
    {
        private readonly StubNeuronHost _grain = new();
        private readonly IInvokable _request;

        public UnstampedNeuronHostCallContext(GrainId targetId, RuntimeInvocation invocation)
        {
            TargetId = targetId;
            var proxy = DispatchProxy.Create<IInvokable, InvokableProxy>();
            ((InvokableProxy)(object)proxy).Invocation = invocation;
            _request = proxy;
        }

        public GrainId TargetId { get; }
        public GrainId? SourceId => null;
        public string InterfaceName => typeof(INeuronHost).FullName!;
        public string MethodName => nameof(INeuronHost.Invoke);
        public object Grain => _grain;
        public IInvokable Request => _request;
        public object? Result { get; set; }
        public Response? Response { get; set; } = Orleans.Serialization.Invocation.Response.FromResult<object?>(null);
        public MethodInfo InterfaceMethod => typeof(INeuronHost).GetMethod(nameof(INeuronHost.Invoke))!;
        public MethodInfo ImplementationMethod => InterfaceMethod;
        public IGrainContext TargetContext => null!;
        public GrainInterfaceType InterfaceType => default;
        public Task Invoke() => Task.CompletedTask;
    }

    private sealed class StubCallerAmbient(Caller? current) : ICallerAmbient
    {
        public Caller? Current => current;
        public IDisposable Enter(Caller caller) => throw new NotImplementedException();
    }

    private sealed class MockIncomingGrainCallContext(
        GrainId targetGrainId,
        GrainId? sourceGrainId = null,
        Func<Task>? onInvoke = null) : IIncomingGrainCallContext
    {
        public GrainId TargetId => targetGrainId;
        public GrainId? SourceId => sourceGrainId;
        public Task Invoke() => onInvoke is not null ? onInvoke() : Task.CompletedTask;

        IGrainContext IIncomingGrainCallContext.TargetContext => null!;
        MethodInfo IIncomingGrainCallContext.ImplementationMethod => null!;
        MethodInfo IGrainCallContext.InterfaceMethod => null!;
        GrainInterfaceType IGrainCallContext.InterfaceType => default;
        string IGrainCallContext.InterfaceName => string.Empty;
        string IGrainCallContext.MethodName => string.Empty;
        IInvokable IGrainCallContext.Request => null!;
        Response? IGrainCallContext.Response { get; set; }
        object? IGrainCallContext.Result { get; set; }
        object IGrainCallContext.Grain => null!;
    }
}
