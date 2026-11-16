using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Units;
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
    public void T2_Edge_InProcessCallerScope_ActiveScope_ElevatesTurnToInternal_AndDisposalReverts()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var scopeType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.InProcessCallerScope", throwOnError: true)!;
        var isActiveProp = scopeType.GetProperty("IsActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var enterMethod = scopeType.GetMethod("Enter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // Baseline: inactive
        Assert.False((bool)isActiveProp.GetValue(null)!);

        // Enter scope: elevated
        using (var scope = (IDisposable)enterMethod.Invoke(null, null)!)
        {
            Assert.NotNull(scope);
            Assert.True((bool)isActiveProp.GetValue(null)!);
        }

        // Disposal: cleanly reverted
        Assert.False((bool)isActiveProp.GetValue(null)!);
    }

    [Fact]
    public void T2_AccessGate_UnauthenticatedCallCarryingNoCaller_ThrowsUnauthorizedAccessException()
    {
        var edge = _fixture.Edge;
        var brain = NewBrain("edge-unauth");
        var targetNeuron = new NeuronId(brain, "test.vault", "default");

        // When no ambient caller is stamped and AuditContractInternal is false, AccessGate throws
        var options = new AccessOptions { UnstampedEdgeIsKernel = false, AuditContractInternal = false };
        var gateType = typeof(AccessPolicy).Assembly.GetType("DigitalBrain.Access.AccessGate", throwOnError: true)!;

        var ambientMock = new StubCallerAmbient(null);
        var gate = Activator.CreateInstance(
            gateType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            [ambientMock, options, new AccessCounters()],
            null)!;

        var admitMethod = gateType.GetMethod("Admit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var binder = new NeuronBinder();
        var boundCall = binder.Bind(typeof(IStubSecurityContract), new RuntimeInvocation("test", nameof(IStubSecurityContract), "default", "DoSomething", []));

        var ex = Assert.Throws<TargetInvocationException>(() =>
            admitMethod.Invoke(gate, [targetNeuron, boundCall]));

        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        Assert.Contains("the call carries no caller", ex.InnerException.Message);
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
    public void T2_Screen_PayloadSanitizer_UntrustedByField_StrippedAndOverwrittenWithHostAssertedBy()
    {
        // Construct args JSON containing author/attacker injected 'by' claim
        var spoofedArgsJson = "{\"by\":\"attacker@evil.com\",\"action\":\"deposit\",\"amount\":500}";
        var rawArgs = JsonSerializer.Deserialize<JsonElement>(spoofedArgsJson);

        // Sanitize via internal ViewNodes.Payload
        var viewNodesType = typeof(Screen<>).Assembly.GetType("DigitalBrain.Units.ViewNodes", throwOnError: true)!;
        var payloadMethod = viewNodesType.GetMethod("Payload", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var sanitizedElement = (JsonElement)payloadMethod.Invoke(null, [rawArgs, null, "alice@intochat.io"])!;

        var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sanitizedElement.GetRawText())!;
        Assert.True(dictionary.ContainsKey("by"));
        Assert.Equal("alice@intochat.io", dictionary["by"].GetString());
        Assert.Equal("deposit", dictionary["action"].GetString());
        Assert.Equal(500, dictionary["amount"].GetInt32());
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
