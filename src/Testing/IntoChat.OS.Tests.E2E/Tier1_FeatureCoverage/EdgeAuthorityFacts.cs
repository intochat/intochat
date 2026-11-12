using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

public sealed class EdgeAuthorityFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public EdgeAuthorityFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void T1_Access_ActorHierarchy_IsStrictlyClosed_WithNoPublicConstructors()
    {
        var actorType = typeof(Actor);

        // Actor constructor is private, sealing the hierarchy
        var publicConstructors = actorType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Empty(publicConstructors);

        var nestedTypes = actorType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        var recordNames = nestedTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // Closed set of records: Person, Agent, App, Module, Scheduler, Kernel
        Assert.Contains("Person", recordNames);
        Assert.Contains("Agent", recordNames);
        Assert.Contains("App", recordNames);
        Assert.Contains("Module", recordNames);
        Assert.Contains("Scheduler", recordNames);
        Assert.Contains("Kernel", recordNames);
    }

    [Fact]
    public void T1_Access_Caller_IsUnforgeable_ImmutableAndNonSerializable()
    {
        var callerType = typeof(Caller);

        // Caller has zero public constructors (can only be minted by EdgeAuthority)
        var publicConstructors = callerType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.Empty(publicConstructors);

        // All properties are read-only (unforgeable without setters or init accessors)
        var properties = callerType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            Assert.False(prop.CanWrite, $"Property {prop.Name} on Caller should not be writable.");
        }

        // Direct deserialization over JSON wire is blocked
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Deserialize<Caller>("{\"Inside\": true, \"Actor\": {\"Kind\": \"Kernel\"}}"));
    }

    [Fact]
    public void T1_Access_EdgeAuthority_MintsDirectCallers_WithInsideFalse()
    {
        var edge = _fixture.Edge;
        var brain = NewBrain("edge-auth");
        var personActor = new Actor.Person("alice_p", "acct_1");

        // 1. Person caller
        var person = edge.Person("alice_p", "acct_1", brain, "conv_1");
        Assert.False(person.Inside);
        Assert.True(person.Direct);
        Assert.Null(person.Via);
        Assert.Equal(personActor, person.Actor);
        Assert.Equal(personActor, person.For);

        // 2. App caller on behalf of person
        var app = edge.App("payment_app", personActor, brain);
        Assert.False(app.Inside);
        Assert.Equal(personActor, app.For);

        // 3. Internal Module caller
        var module = edge.Module("auth_module", brain);
        Assert.True(module.Inside);
        Assert.False(module.Direct);
    }

    [Fact]
    public void T1_Access_InProcessCallerScope_GrantsInternalPrivilege_ForHostTurns()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var scopeType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.InProcessCallerScope", throwOnError: true)!;
        var isActiveProp = scopeType.GetProperty("IsActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var enterMethod = scopeType.GetMethod("Enter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // Initially inactive
        Assert.False((bool)isActiveProp.GetValue(null)!);

        // Scope activation
        using (var scope = (IDisposable)enterMethod.Invoke(null, null)!)
        {
            Assert.NotNull(scope);
            Assert.True((bool)isActiveProp.GetValue(null)!);
        }

        // Reverts after disposal
        Assert.False((bool)isActiveProp.GetValue(null)!);
    }

    [Fact]
    public void T1_Access_AccessPolicy_Decide_EnforcesKernelBypass_BrainIsolation_AndInternalAccessFences()
    {
        var edge = _fixture.Edge;
        var brainA = new BrainId("brain-alpha");
        var brainB = new BrainId("brain-beta");
        var targetNeuronA = new NeuronId(brainA, "custom.test", "default");

        // 1. Kernel bypasses all checks
        var kernelCaller = edge.Kernel(brainA);
        var kernelDecision = AccessPolicy.Decide(kernelCaller, targetNeuronA, OperationAccess.Internal);
        Assert.Equal(AccessDecision.Allow, kernelDecision);

        // 2. Cross-brain call is denied with OutsideBrain
        var outsidePersonCaller = edge.Person("bob", "acct_2", brainB);
        var outsideDecision = AccessPolicy.Decide(outsidePersonCaller, targetNeuronA, OperationAccess.Public);
        Assert.False(outsideDecision.Allowed);
        Assert.Equal(AccessDenial.OutsideBrain, outsideDecision.Denial);

        // 3. External client calling Internal operation is denied with InternalOnly
        var externalPersonCaller = edge.Person("alice", "acct_1", brainA);
        var internalDecision = AccessPolicy.Decide(externalPersonCaller, targetNeuronA, OperationAccess.Internal);
        Assert.False(internalDecision.Allowed);
        Assert.Equal(AccessDenial.InternalOnly, internalDecision.Denial);

        // 4. Module inside cluster calling Internal operation is allowed
        var moduleCaller = edge.Module("auth_mod", brainA);
        var moduleDecision = AccessPolicy.Decide(moduleCaller, targetNeuronA, OperationAccess.Internal);
        Assert.Equal(AccessDecision.Allow, moduleDecision);
    }

    [Fact]
    public async Task T1_Access_CallerEdgeFilter_ExternalClient_StripsForgedClaimsAndRejectsInternalActors()
    {
        var orleansAssembly = typeof(RuntimeOptions).Assembly;
        var filterType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerEdgeFilter", throwOnError: true)!;
        var wireType = orleansAssembly.GetType("DigitalBrain.Runtime.Orleans.Access.CallerWire", throwOnError: true)!;

        var filter = (IIncomingGrainCallFilter)Activator.CreateInstance(filterType)!;

        var targetNeuron = GrainId.Create("neuron", "test-brain/vault/default");
        var internalGrain = GrainId.Create("neuron", "test-brain/worker/0");
        var externalClientGrain = GrainId.Create("sys.client", "client-connection-99");

        // Scenario 1: External Client attempting to claim Kernel actor kind -> REJECTED
        var forgedKernelWire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(forgedKernelWire, (byte)5); // ActorKernel = 5
        wireType.GetField("Brain")!.SetValue(forgedKernelWire, "test-brain");
        wireType.GetField("Inside")!.SetValue(forgedKernelWire, false);

        RequestContext.Set("db.access.caller", forgedKernelWire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.False(executed, "External caller claiming ActorKernel must be rejected before invocation.");
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }

        // Scenario 2: External Client via sys.client claiming Module actor kind -> REJECTED
        var forgedModuleWire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(forgedModuleWire, (byte)3); // ActorModule = 3
        wireType.GetField("Brain")!.SetValue(forgedModuleWire, "test-brain");
        wireType.GetField("Name")!.SetValue(forgedModuleWire, "fake-module");

        RequestContext.Set("db.access.caller", forgedModuleWire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: externalClientGrain, () =>
            {
                executed = true;
                return Task.CompletedTask;
            });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
            Assert.False(executed, "sys.client caller claiming ActorModule must be rejected.");
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }

        // Scenario 3: External Client claiming Inside=true and Via!=null -> STRIPPED
        var forgedClientWire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(forgedClientWire, (byte)0); // ActorPerson = 0
        wireType.GetField("Principal")!.SetValue(forgedClientWire, "alice");
        wireType.GetField("Account")!.SetValue(forgedClientWire, "acct_1");
        wireType.GetField("Brain")!.SetValue(forgedClientWire, "test-brain");
        wireType.GetField("Inside")!.SetValue(forgedClientWire, true); // Forgery attempt
        wireType.GetField("Via")!.SetValue(forgedClientWire, "test-brain/trusted/0"); // Forgery attempt

        RequestContext.Set("db.access.caller", forgedClientWire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: null, () =>
            {
                executed = true;
                var inside = (bool)wireType.GetField("Inside")!.GetValue(forgedClientWire)!;
                var via = wireType.GetField("Via")!.GetValue(forgedClientWire);
                Assert.False(inside, "External client Inside claim must be stripped to false.");
                Assert.Null(via);
                return Task.CompletedTask;
            });

            await filter.Invoke(context);
            Assert.True(executed, "Legitimate person caller with stripped claims must proceed.");
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }

        // Scenario 4: Internal cluster grain -> STAMPED with Inside=true
        var internalWire = Activator.CreateInstance(wireType)!;
        wireType.GetField("Actor")!.SetValue(internalWire, (byte)3); // ActorModule = 3
        wireType.GetField("Name")!.SetValue(internalWire, "auth-service");
        wireType.GetField("Brain")!.SetValue(internalWire, "test-brain");
        wireType.GetField("Inside")!.SetValue(internalWire, false); // Arriving unstamped

        RequestContext.Set("db.access.caller", internalWire);
        try
        {
            var executed = false;
            var context = new MockIncomingGrainCallContext(targetNeuron, sourceGrainId: internalGrain, () =>
            {
                executed = true;
                var inside = (bool)wireType.GetField("Inside")!.GetValue(internalWire)!;
                Assert.True(inside, "Internal grain call must be stamped with Inside=true.");
                return Task.CompletedTask;
            });

            await filter.Invoke(context);
            Assert.True(executed, "Internal grain call must proceed.");
        }
        finally
        {
            RequestContext.Remove("db.access.caller");
        }
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
