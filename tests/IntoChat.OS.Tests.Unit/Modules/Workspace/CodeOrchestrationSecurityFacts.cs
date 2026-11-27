using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace;

public sealed class CodeOrchestrationSecurityFacts
{
    private const string CleanNotificationScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                await os.Notifications.NotifyAsync("Digest", "Ready", NotificationPriority.Normal, ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Notified",
                    Artifacts: [],
                    Metrics: new Dictionary<string, double> { ["count"] = 1 });
            }
        }
        """;

    private sealed class RecordingSandboxBridge(Func<int, SandboxDispatchResult>? onDispatch = null) : ISandboxRuntimeBridge
    {
        private int _dispatchCount;

        public int DeployCount { get; private set; }
        public int DispatchCount => _dispatchCount;
        public List<string[]> DeployedScopes { get; } = [];
        public List<string> DispatchedCommands { get; } = [];
        public List<string> UnloadedApps { get; } = [];

        public Task<SandboxDeploymentResult> DeployPackageAsync(
            string brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, CancellationToken cancellationToken = default)
        {
            DeployCount++;
            return Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));
        }

        public Task<SandboxDeploymentResult> DeployScopedPackageAsync(
            BrainId brainId, AppPackage package, byte[] assemblyBytes, string? stateJson, string? settingsJson, IReadOnlyList<string> allowedContracts, CancellationToken cancellationToken = default)
        {
            DeployCount++;
            DeployedScopes.Add([.. allowedContracts]);
            return Task.FromResult(new SandboxDeploymentResult(true, null, [], [], [CodeOrchestrationScriptWrapper.Command], null));
        }

        public Task<SandboxDispatchResult> DispatchSignalAsync(string brainId, string appKey, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(false, null, null, null, "not used"));

        public Task<SandboxDispatchResult> DispatchScheduleAsync(string brainId, string appKey, string cron, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxDispatchResult(false, null, null, null, "not used"));

        public Task<SandboxDispatchResult> DispatchCommandAsync(string brainId, string appKey, string command, string? payloadJson = null, CancellationToken cancellationToken = default)
        {
            var attempt = ++_dispatchCount;
            DispatchedCommands.Add(command);
            if (onDispatch is not null)
            {
                return Task.FromResult(onDispatch(attempt));
            }

            var payload = JsonSerializer.Serialize("""{"status":"succeeded","summary":"ok","artifacts":[],"metrics":{}}""");
            return Task.FromResult(new SandboxDispatchResult(true, payload, null, null, null));
        }

        public Task<DigitalBrain.UI.View?> RenderAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
            => Task.FromResult<DigitalBrain.UI.View?>(null);

        public Task ForwardSignalAsync(string brainId, string signalName, string payloadJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
        {
            UnloadedApps.Add(appKey);
            return Task.CompletedTask;
        }
    }

    private static TestOS CreateTestOs(ICodeOrchestrationAstSanitizer sanitizer, ISandboxRuntimeBridge bridge)
    {
        var services = new ServiceCollection()
            .AddSingleton(new AppPackageCompiler())
            .AddSingleton(sanitizer)
            .AddSingleton(bridge)
            .BuildServiceProvider();

        return TestOS.Local(o => o.Services = services)
            .Register<CodeOrchestrationSynapse>();
    }

    [Fact]
    public async Task FailsClosedWhenUnavailableAstSanitizerIsInjected_NeverReachesSandbox()
    {
        var direct = UnavailableCodeOrchestrationAstSanitizer.Instance.Validate(CleanNotificationScript);
        Assert.False(direct.IsValid);
        Assert.Contains(direct.Violations, v => v.RuleId == "AST000");

        var bridge = new RecordingSandboxBridge();
        await using var os = CreateTestOs(UnavailableCodeOrchestrationAstSanitizer.Instance, bridge);
        var brain = new BrainId("orch-fail-closed");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-unavailable-ast");
        var starter = os.Get<IFlowStarter>(flowId);

        using var _ = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Must fail closed without Roslyn AST sanitizer",
            AllowedContracts = ["notifications"],
            PreauthoredScript = CleanNotificationScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Fail Closed", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Failed, head.Status);
        var inner = JsonSerializer.Deserialize<CodeOrchestrationInner>(head.InnerJson, CodeOrchestrationSynapse.Json)!;
        Assert.NotNull(inner.Error);
        Assert.Contains("AST000", inner.Error, StringComparison.Ordinal);
        Assert.Equal(0, bridge.DeployCount);
        Assert.Equal(0, bridge.DispatchCount);
    }

    [Theory]
    [InlineData("using System.Reflection;\npublic static class OrchestrationScript { }")]
    [InlineData("public static class OrchestrationScript { public static void X() { _ = System.Reflection.Assembly.Load(\"m\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { _ = System.Type.GetType(\"System.IO.File\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { System.Diagnostics.Process.Start(\"cmd.exe\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { _ = System.IO.File.ReadAllText(\"secret.txt\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { using var c = new System.Net.Sockets.TcpClient(); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { using var h = new System.Net.Http.HttpClient(); } }")]
    [InlineData("public static class OrchestrationScript { public static unsafe void X() { int v = 1; int* p = &v; } }")]
    [InlineData("public static class OrchestrationScript { [System.Runtime.InteropServices.DllImport(\"kernel32.dll\")] public static extern int F(); }")]
    public void AstSanitizer_RejectsUnsafeAndEscapeConstructs(string maliciousSource)
    {
        var sanitizer = new CodeOrchestrationAstSanitizer();
        var result = sanitizer.Validate(maliciousSource);
        Assert.False(result.IsValid, $"Expected AST rejection for: {maliciousSource}");
        Assert.NotEmpty(result.Violations);
    }

    [Fact]
    public void ComputeEffectiveScope_StrictlyIntersectsAllowedContractsAndExcludesWildcards()
    {
        // Cannot widen beyond AllowedContracts.
        var narrowed = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: ["notifications"],
            inferredContracts: ["notifications", "folder", "github"]);
        Assert.Equal(["notifications"], narrowed);

        // Disjoint inferred contracts produce empty scope (never widen back to AllowedContracts).
        var disjoint = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: ["notifications"],
            inferredContracts: ["folder"]);
        Assert.Empty(disjoint);

        // Zero inferred contracts produce zero-contract scope.
        var zeroInferred = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: ["notifications", "github"],
            inferredContracts: []);
        Assert.Empty(zeroInferred);

        // Wildcards (*/*, *, prefix/*) and authority contracts are always excluded.
        var strippedWildcards = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: ["*/*", "*", "notifications/*", "notifications", "DigitalBrain.Confirmation.IConfirmationRequests"],
            inferredContracts: ["*/*", "notifications", "DigitalBrain.Confirmation.IConfirmationRequests"]);
        Assert.Equal(["notifications"], strippedWildcards);

        // Wildcard-only AllowedContracts grants nothing.
        var wildcardOnly = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: ["*/*", "*"],
            inferredContracts: ["notifications"]);
        Assert.Empty(wildcardOnly);

        // Unconstrained mode still strips wildcards and authority contracts.
        var unconstrained = CodeOrchestrationSynapse.ComputeEffectiveScope(
            allowedContracts: null,
            inferredContracts: ["*/*", "*", "notifications", "IntoChat.OS.Modules.Identity.IBrainKeys"]);
        Assert.Equal(["notifications"], unconstrained);
    }

    [Fact]
    public async Task PersonOnlyCall_DurablySuspendsOnConfirmationPlanWithRequiresPerson_UntilApproved()
    {
        const string personOnlyScript = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;
            using IntoChat.OS.Tests.Unit.Sandbox;

            public static class OrchestrationScript
            {
                public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    var widgets = os.Get<IDemoWidgetContract>("default");
                    await widgets.DeleteWidgetAsync("w-42", ct);
                    return new OrchestrationResult("succeeded", "deleted");
                }
            }
            """;

        var bridge = new RecordingSandboxBridge();
        await using var os = CreateTestOs(new CodeOrchestrationAstSanitizer(), bridge);
        var brain = new BrainId("orch-person-only");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-person-only-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var _ = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Delete widget via [PersonOnly] contract",
            AllowedContracts = ["demo.widgets"],
            PreauthoredScript = personOnlyScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Delete Widget", "owner", null, innerJson);
        await os.Idle();

        // Must be durably suspended BEFORE any sandbox execution occurs.
        var suspended = await starter.Head();
        Assert.Equal(FlowStatus.Waiting, suspended.Status);
        Assert.Equal(0, bridge.DeployCount);
        Assert.Equal(0, bridge.DispatchCount);

        var inbox = os.Get<IConfirmationInbox>(new NeuronId(brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        var snapshot = await inbox.Read();
        var pending = Assert.Single(snapshot.Pending, p => p.Plan.Kind == "code-orchestration.person-only");
        Assert.True(pending.Plan.RequiresPerson);
        Assert.Contains("DeleteWidgetAsync", pending.Plan.Body, StringComparison.Ordinal);

        // Once the person approves, execution proceeds through the sandbox.
        await inbox.Resolve(pending.Plan.Id, approve: true);
        await os.Idle();

        var completed = await starter.Head();
        Assert.Equal(FlowStatus.Completed, completed.Status);
        Assert.Equal(1, bridge.DeployCount);
        Assert.Equal(1, bridge.DispatchCount);
    }

    [Fact]
    public async Task Execution_DelegatesStrictlyThroughSandboxBridgeWithScopedContracts()
    {
        var bridge = new RecordingSandboxBridge();
        await using var os = CreateTestOs(new CodeOrchestrationAstSanitizer(), bridge);
        var brain = new BrainId("orch-sandbox-boundary");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-sandbox-1");
        var starter = os.Get<IFlowStarter>(flowId);

        using var _ = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        var innerJson = JsonSerializer.Serialize(new CodeOrchestrationInner
        {
            Goal = "Send scoped notification",
            AllowedContracts = ["github", "notifications", "*/*"],
            PreauthoredScript = CleanNotificationScript,
        }, CodeOrchestrationSynapse.Json);

        await starter.StartFromJson("Scoped Sandbox", "owner", null, innerJson);
        await os.Idle();

        var head = await starter.Head();
        Assert.Equal(FlowStatus.Completed, head.Status);
        Assert.Equal(1, bridge.DeployCount);
        Assert.Equal(1, bridge.DispatchCount);
        Assert.Equal([CodeOrchestrationScriptWrapper.Command], bridge.DispatchedCommands);
        Assert.Equal(["notifications"], Assert.Single(bridge.DeployedScopes));
        Assert.Single(bridge.UnloadedApps);

        // Verify CodeOrchestrationSynapse depends on ISandboxRuntimeBridge and does not load assemblies in-process.
        var ctor = Assert.Single(typeof(CodeOrchestrationSynapse).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(ISandboxRuntimeBridge));
    }

    [Fact]
    public void BuildCrystallizedUnitSource_EscapesInjectionPayloadsInTitleAndContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        var payloads = new[]
        {
            "foo\"; System.Diagnostics.Process.Start(\"calc\"); //",
            "foo\npublic static class Evil {}",
            "foo\r\n// injected",
            "trailing-backslash\\",
            "ctrl\u0000\u0007char",
        };

        foreach (var payload in payloads)
        {
            var source = CodeOrchestrationSynapse.BuildCrystallizedUnitSource(
                "daily.digest",
                "pinned-daily-digest",
                payload,
                ["notifications", "bad\n//injected"]);

            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
            var diagnostics = tree.GetDiagnostics(ct).Where(static d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
            Assert.Empty(diagnostics);

            // Only the expected single state record and neuron class may exist — never an injected type.
            var types = tree.GetRoot(ct).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().Select(static t => t.Identifier.ValueText).ToArray();
            Assert.Equal(["PinnedDailyDigestState", "PinnedDailyDigest"], types);
            Assert.DoesNotContain("Evil", types);
        }
    }

    [Theory]
    [InlineData("identity.brain-keys")]
    [InlineData("os.settings")]
    [InlineData("social.room")]
    [InlineData("flow.engineer")]
    [InlineData("ai.memory")]
    [InlineData("mcp.tools")]
    [InlineData("ino.studio")]
    [InlineData("tasks.board")]
    [InlineData("desktop.shell")]
    [InlineData("workspace.files")]
    public void NormalizeAppAlias_RefusesReservedSystemPrefixes(string reservedAlias)
    {
        Assert.Throws<UnauthorizedAccessException>(() => CodeOrchestrationSynapse.NormalizeAppAlias(reservedAlias));
    }

    [Fact]
    public void NormalizeAppAlias_RefusesOverlongAliases()
    {
        var tooLong = new string('a', 65);
        Assert.Throws<ArgumentException>(() => CodeOrchestrationSynapse.NormalizeAppAlias(tooLong));
    }

    [Fact]
    public async Task CrystallizeAsync_RefusesNonPersonCallers()
    {
        var ct = TestContext.Current.CancellationToken;
        var bridge = new RecordingSandboxBridge();
        await using var os = CreateTestOs(new CodeOrchestrationAstSanitizer(), bridge);
        var brain = new BrainId("orch-person-only-crystallize");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-cryst-auth");
        var neuron = os.Get<ICodeOrchestration>(flowId);

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            _ = await neuron.RunAsync("Digest", "Run digest", allowedContracts: ["notifications"], preauthoredScript: CleanNotificationScript, ct: ct);
            await os.Idle();
        }

        // Agent caller must be refused ([PersonOnly]).
        using (os.Edge.Enter(os.Edge.Agent(new DigitalBrain.Access.Actor.Person("owner", "owner"), brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }

        // Cross-brain person caller must also be refused.
        using (os.Edge.Enter(os.Edge.Person("other", "other", new BrainId("other-brain"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }
    }

    public interface ITestInoCrystallizeRelay
    {
        Task<CrystallizedAppReceipt> RelayCrystallizeAsync(NeuronId flowId, string alias, string title, CancellationToken ct = default);
    }

    [Neuron("app.ino")]
    private sealed class TestInoCrystallizeRelay : Neuron, ITestInoCrystallizeRelay
    {
        public Task<CrystallizedAppReceipt> RelayCrystallizeAsync(NeuronId flowId, string alias, string title, CancellationToken ct = default)
            => Context.Get<ICodeOrchestration>(flowId).CrystallizeAsync(alias, title, ct);
    }

    [Fact]
    public async Task CrystallizeAsync_EnforcesViaInoShellRelayBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var bridge = new RecordingSandboxBridge();
        await using var os = CreateTestOs(new CodeOrchestrationAstSanitizer(), bridge)
            .Register<TestInoCrystallizeRelay>();

        var brain = new BrainId("orch-via-ino-boundary");
        var otherBrain = new BrainId("other-brain");
        var flowId = new NeuronId(brain, CodeOrchestrationSynapse.Alias, "run-cryst-relay");
        var neuron = os.Get<ICodeOrchestration>(flowId);
        var sameBrainIno = new NeuronId(brain, "app.ino", "default");
        var sameBrainFriends = new NeuronId(brain, "app.friends", "default");
        var crossBrainIno = new NeuronId(otherBrain, "app.ino", "default");

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            _ = await neuron.RunAsync("Digest", "Run digest", allowedContracts: ["notifications"], preauthoredScript: CleanNotificationScript, ct: ct);
            await os.Idle();
        }

        // REJECT: null / unstamped caller
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));

        // REJECT: Conversation != null even when Inside=true and Via=app.ino
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", brain, via: sameBrainIno, conversation: "main", inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }

        // REJECT: Inside == false with Via=app.ino
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", brain, via: sameBrainIno, conversation: null, inside: false)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }

        // REJECT: Via.Type != "app.ino" (e.g., app.friends)
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", brain, via: sameBrainFriends, conversation: null, inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }

        // REJECT: Via.Brain != Id.Brain (cross-brain app.ino)
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", brain, via: crossBrainIno, conversation: null, inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct));
        }

        // REJECT: Actor.Agent relayed through a real same-brain app.ino hop (Inside=true, Via=app.ino)
        var relay = os.Get<ITestInoCrystallizeRelay>(sameBrainIno);
        using (os.Edge.Enter(os.Edge.Agent(new DigitalBrain.Access.Actor.Person("owner", "owner"), brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => relay.RelayCrystallizeAsync(flowId, "daily.digest", "Daily Digest", ct));
        }

        // ALLOW: Direct person (Inside=false, Via=null, Conversation=null)
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", brain)))
        {
            var directReceipt = await neuron.CrystallizeAsync("daily.digest", "Daily Digest", ct);
            Assert.True(directReceipt.Succeeded);
        }

        // ALLOW: Same-brain app.ino-relayed person (Actor.Person, Inside=true, Conversation=null, Via={Brain==Id.Brain, Type=="app.ino"})
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", brain, via: sameBrainIno, conversation: null, inside: true)))
        {
            var relayedReceipt = await neuron.CrystallizeAsync("daily.digest.relayed", "Daily Digest Relayed", ct);
            Assert.True(relayedReceipt.Succeeded);
        }
    }
}
