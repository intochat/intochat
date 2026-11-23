using System.Reflection;
using System.Text;
using DigitalBrain.Flows;
using DigitalBrain.Invocation;
using IntoChat.OS.Host.Kernel;
using IntoChat.Aspire.Candidates;
using IntoChat.Aspire.Deployment;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using IntoChat.Aspire;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// Frozen stored state of the Aspire module. A neuron's state is written through RuntimeProtocol as JSON with no type name
// (KernelState.Write; the Orleans runtime's BrainLog and the local file store use the same bytes), so the JSON files pin what
// is on disk. AspireDeploymentState also carries [GenerateSerializer]: its Orleans bytes are pinned too, because the
// local file store falls back to the Orleans serializer for a state JSON cannot write. UPDATE_GOLDEN=1 rewrites the files.
public sealed class AspireStoredStateFacts
{
    private const string Namespace = "IntoChat.Aspire";
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static AspireTelemetryState Telemetry() => new(
        [new AspireLogEntry(At, "api", "Information", "started", null), new AspireLogEntry(At.AddSeconds(1), "api", "Error", "failed", "System.InvalidOperationException: boom")],
        [new AspireTraceSpan("t1", "s1", "api", "GET /", "Ok", 1500, null), new AspireTraceSpan("t1", "s2", "api", "POST /x", "Error", 2500, "timeout")]);

    private static AspireTelemetryState TelemetryWithPendingCommand() => Telemetry() with
    {
        PendingCommands = [new AspirePendingCommand("aspire.command:web-searxng:resource-restart:0123456789abcdef0123456789abcdef", "web-searxng", "web-searxng", "resource-restart", At)],
    };

    // DeployFlow is a Flow<DeployInner>: what it stores is the framework's FlowState with the roll's own state as Inner.
    private static FlowState<DeployInner> DeployFlowState() => new(
        "flow-1", "Deploy 4567def", "local", "trace-1", FlowStatus.Waiting,
        ["order", "roll:os-a:mark", "roll:os-a:restart", "roll:os-a:ready", "roll:os-b:mark"], "roll:os-b:restart",
        new Waiting("roll:os-b:restart", WaitReason.Schedule, At.AddMinutes(1), null), [], null, At, null,
        new DeployInner
        {
            TargetSha = "4567def",
            Remaining = ["os-a", "os-b"],
            Order = ["os-b", "os-a"],
            Rolled = ["os-a"],
            PreviousSha = "0123abc",
            RestartIssued = new RestartIssued("os-b", At),
        })
    { Interruptions = 1 };

    private static CandidateState Candidate() => new()
    {
        Branch = "candidate/golden",
        Summary = "A golden candidate",
        Stage = CandidateStage.Failed,
        Endpoints = new CandidateEndpoints("http://localhost:9001/mcp", "http://localhost:9002"),
        Plan = new VerificationPlan([new NeuronExpectation("aspire", "b1/aspire", "Read", "[]", "ok")], ["span.one", "span.two"], TimeSpan.FromMinutes(5)),
        Report = new VerificationReport([new VerificationCheck("calls", true, "all answered"), new VerificationCheck("spans", false, "span.two missing")], TimeSpan.FromSeconds(42)),
        Failure = "verification failed: spans",
        History = [CandidateStage.Proposed, CandidateStage.Queued, CandidateStage.Building, CandidateStage.Failed],
    };

    private static CandidateQueueState Queue() => new() { Live = "c1", Queue = ["c2", "c3"] };

    private static AspireDeploymentState Deployment() => new()
    {
        CurrentSha = "0123abc",
        Stage = DeploymentStage.RollingBack,
        TargetSha = "4567def",
        Remaining = ["os-a", "os-b"],
        Rolled = ["os-c"],
        PreviousSha = "89abcde",
        Handover = "os-d",
        Failure = "os-b did not start",
        Draining = "os-b",
        HandoverAt = At,
        FlowId = "flow-1",
        FailedInstance = "os-b",
    };

    private static byte[] Journal(object value) => RuntimeProtocol.SerializeToUtf8Bytes(value, value.GetType());

    private static Serializer Orleans()
        => new ServiceCollection().AddSerializer(builder => builder.AddAssembly(typeof(AspireModule).Assembly)).BuildServiceProvider().GetRequiredService<Serializer>();

    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "AspireGolden", name);

    private static bool Updating => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";

    private static void Pin(string name, byte[] actual)
    {
        Assert.NotEmpty(actual);
        if (Updating)
        {
            var source = Path.Combine(Path.GetDirectoryName(IntoChat.OS.Aspire.Hosting.Tests.Unit.DeploymentSnapshot.ProjectPath("IntoChat.OS.Tests.Unit"))!, "Components", "Aspire", "Golden", name);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, actual);
            Directory.CreateDirectory(Path.GetDirectoryName(Golden(name))!);
            File.WriteAllBytes(Golden(name), actual);
            return;
        }
        Assert.True(File.Exists(Golden(name)), $"Golden file {name} is missing; run with UPDATE_GOLDEN=1 once.");
        Assert.Equal(File.ReadAllBytes(Golden(name)), actual);
    }

    private static T ReadJournal<T>(string name) where T : class
        => (T)RuntimeProtocol.Deserialize(File.ReadAllBytes(Golden(name)), typeof(T))!;

    [Fact]
    public void TheTelemetryStateIsStoredAsFrozenJson()
    {
        Pin("AspireTelemetryState.json", Journal(Telemetry()));
        var read = ReadJournal<AspireTelemetryState>("AspireTelemetryState.json");
        Assert.Equivalent(Telemetry(), read, strict: true);
    }

    [Fact]
    public void TheTelemetryStateWithAPendingCommandIsStoredAsFrozenJson()
    {
        Pin("AspireTelemetryState.pending.json", Journal(TelemetryWithPendingCommand()));
        var read = ReadJournal<AspireTelemetryState>("AspireTelemetryState.pending.json");
        Assert.Equivalent(TelemetryWithPendingCommand(), read, strict: true);
        Assert.Equal(TelemetryWithPendingCommand().PendingCommands, read.PendingCommands);
    }

    [Fact]
    public void TheDeployFlowStateIsStoredAsFrozenJson()
    {
        Pin("DeployFlowState.json", Journal(DeployFlowState()));
        var read = ReadJournal<FlowState<DeployInner>>("DeployFlowState.json");
        Assert.Equivalent(DeployFlowState().Inner, read.Inner, strict: true);
        Assert.Equal(DeployFlowState().Done, read.Done);
        Assert.Equal(DeployFlowState().Waiting, read.Waiting);
        Assert.Equal(1, read.Interruptions);
    }

    [Fact]
    public void TheCandidateStateIsStoredAsFrozenJson()
    {
        Pin("CandidateState.json", Journal(Candidate()));
        var read = ReadJournal<CandidateState>("CandidateState.json");
        Assert.Equivalent(Candidate(), read, strict: true);
        Assert.Equal(CandidateStage.Failed, read.Stage);
    }

    [Fact]
    public void TheCandidateQueueStateIsStoredAsFrozenJson()
    {
        Pin("CandidateQueueState.json", Journal(Queue()));
        var read = ReadJournal<CandidateQueueState>("CandidateQueueState.json");
        Assert.Equal("c1", read.Live);
        Assert.Equal(["c2", "c3"], read.Queue);
    }

    [Fact]
    public void TheDeploymentStateIsStoredAsFrozenJson()
    {
        Pin("DeploymentState.json", Journal(Deployment()));
        var read = ReadJournal<AspireDeploymentState>("DeploymentState.json");
        Assert.Equivalent(Deployment(), read, strict: true);
    }

    // Typed, Orleans writes field ids only; as object (a polymorphic slot) it writes the type's [Alias] and never its CLR name.
    [Fact]
    public void TheDeploymentStateHasFrozenOrleansBytesTypedAndAsObject()
    {
        var serializer = Orleans();
        Pin("DeploymentState.orleans.bin", serializer.SerializeToArray(Deployment()));
        Pin("DeploymentState.orleans-object.bin", serializer.SerializeToArray<object>(Deployment()));
        Assert.Equivalent(Deployment(), serializer.Deserialize<AspireDeploymentState>(File.ReadAllBytes(Golden("DeploymentState.orleans.bin"))), strict: true);
        Assert.Equivalent(Deployment(), serializer.Deserialize<object>(File.ReadAllBytes(Golden("DeploymentState.orleans-object.bin"))), strict: true);
        Assert.Contains("aspire.deployment.state", Encoding.UTF8.GetString(File.ReadAllBytes(Golden("DeploymentState.orleans-object.bin"))), StringComparison.Ordinal);
    }

    [Fact]
    public void NoStoredFormNamesAClrTypeOrAssembly()
    {
        foreach (var name in new[] { "AspireTelemetryState.json", "AspireTelemetryState.pending.json", "DeployFlowState.json", "CandidateState.json", "CandidateQueueState.json", "DeploymentState.json", "DeploymentState.orleans.bin", "DeploymentState.orleans-object.bin" })
        {
            Assert.True(File.Exists(Golden(name)), $"Golden file {name} is missing; run with UPDATE_GOLDEN=1 once.");
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(Golden(name)));
            Assert.DoesNotContain("IntoChat", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Modules.Microsoft", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EverySerializableAspireTypeHasAnAlias()
    {
        var types = typeof(IAspire).Assembly.GetTypes()
            .Concat(typeof(AspireModule).Assembly.GetTypes())
            .Distinct()
            .Where(type => type.Namespace is { } ns && (ns == Namespace || ns.StartsWith(Namespace + ".", StringComparison.Ordinal)))
            .Where(type => type.GetCustomAttribute<GenerateSerializerAttribute>(inherit: false) is not null)
            .ToArray();

        Assert.NotEmpty(types);
        var missing = types.Where(type => string.IsNullOrWhiteSpace(type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias)).Select(type => type.FullName).ToArray();
        Assert.True(missing.Length == 0, "Serializable types without [Alias]: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("aspire", typeof(IAspire))]
    [InlineData("aspire.candidate", typeof(ICandidate))]
    [InlineData("aspire.candidates", typeof(ICandidates))]
    [InlineData("aspire.deployment", typeof(IDeployment))]
    public void TheContractsResolveByAlias(string alias, Type expected)
    {
        var compiled = new CompiledTypes([typeof(IAspire).Assembly]);

        var entry = Assert.Single(compiled.Entries, entry => entry.Alias == alias);
        var contract = compiled.Contract(entry.Name);

        Assert.Same(expected, contract);
    }
}
