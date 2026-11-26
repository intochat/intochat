using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Integration;

[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
public interface IAgentAppTestToolSource : IToolSource
{
    const string TypeName = "testtool";
}

[Neuron(IAgentAppTestToolSource.TypeName)]
public sealed class AgentAppTestToolSourceNeuron : Neuron, IAgentAppTestToolSource
{
    private static readonly ConcurrentDictionary<string, ToolSet> ToolSets = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<(string Key, string Tool, string Arguments)> Invocations = new();

    public static void Configure(string fullKey, ToolSet tools) => ToolSets[fullKey] = tools;

    public static void Clear()
    {
        ToolSets.Clear();
        while (Invocations.TryDequeue(out _)) { }
    }

    public static IReadOnlyList<(string Key, string Tool, string Arguments)> Calls => [.. Invocations];

    public Task<ToolSet> Tools(CancellationToken ct = default)
        => Task.FromResult(ToolSets.TryGetValue(Id.FullKey(), out var set) ? set : new ToolSet(Id.Key, []));

    public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
    {
        Invocations.Enqueue((Id.FullKey(), tool, arguments));
        return Task.FromResult(new ToolResult(false, $"ran:{tool}:{arguments}", []));
    }
}

[Collection(nameof(InoSiloCollection))]
public sealed class AgentAppJourneyFacts(InoSiloFixture fixture)
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task PureDataAgentAppWorksThroughBothScreenDirectoryAndAppUiEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("agentapp");
        var brainId = new BrainId(brain);
        using var person = fixture.AsPerson(brain);

        fixture.Model.ClearRequests();
        fixture.Model.Reply("Treg reply!");

        var app = SiloNeurons.Get<IApp>(fixture.Services, new NeuronId(brainId, AppGrainType.Name, "apps/treg"));
        var manifest = new AppManifest(
            Title: "Treg Assistant",
            Description: "Tracks treg workspace issues.",
            Operations: [],
            Settings: [],
            Slots: [new AppSlot("treg", Connector: "mcp", Required: true)],
            Agent: new AppAgent(
                Instructions: "You are the treg agent.",
                Starters: ["Show open bugs"],
                Tools: ["treg"]));

        var created = await app.Create(manifest, "");
        Assert.Equal(AppStatus.Live, created.Status);
        Assert.NotNull(created.Manifest.Agent);
        fixture.ObserveInstance(AppGrainType.Name, $"{brain}/apps/treg");

        // Listed in IInstalledApps as an agent tile.
        var installed = SiloNeurons.Get<IInstalledApps>(fixture.Services, PlainNeuronContracts.IdOf(typeof(IInstalledApps), $"{brain}/{IInstalledApps.DefaultKey}"));
        var entries = await installed.List();
        var tregEntry = Assert.Single(entries, e => e.Id == "treg");
        Assert.Equal("agent", tregEntry.Kind);
        Assert.Equal("Treg Assistant", tregEntry.Title);

        // Bind slot to canonical MCP connection key.
        var canonicalKey = $"{brain}/connection/mcp/treg-main";
        var bindOutcome = await app.BindSlots(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = canonicalKey },
            ct);
        Assert.Equal(SlotBindingStatus.Ok, bindOutcome.Status);
        Assert.Equal(canonicalKey, bindOutcome.App!.Connections["treg"]);

        // Unified IScreenTargetResolver from DI resolves pure-data agent app to agentapp/<name>.
        var resolver = fixture.Services.GetRequiredService<IScreenTargetResolver>();
        var target = await resolver.ResolveAsync(brainId, "treg", ct);
        Assert.NotNull(target);
        Assert.Equal(IScreenTargetResolver.AgentAppKind, target.Kind);
        Assert.Equal(new NeuronId(brainId, "agentapp", "treg"), target.ScreenId);

        // Path 1: IScreenDirectory Read + Act.
        var directory = SiloNeurons.Get<IScreenDirectory>(fixture.Services, new NeuronId(brainId, IScreenDirectory.TypeName, "main"));
        var snapshot = await directory.Read("treg");
        Assert.NotNull(snapshot);
        Assert.Equal(IScreenTargetResolver.AgentAppKind, snapshot.Kind);
        Assert.NotNull(snapshot.View);
        var snapJson = JsonSerializer.Serialize(snapshot.View, AppUiEndpoints.ViewJson);
        Assert.Contains("Treg Assistant", snapJson, StringComparison.Ordinal);
        Assert.Contains("Show open bugs", snapJson, StringComparison.Ordinal);

        var actResult = await directory.Act("treg", "starter:0");
        Assert.True(actResult.Accepted, actResult.Message);

        // Wait for the real ConversationNeuron run to finish and AgentAppScreen to project into ChatThreadNeuron.
        var proxies = fixture.Services.GetRequiredService<INeuronProxies>();
        var hosted = fixture.Services.GetService<IHostedAppTypes>();
        var caller = fixture.Edge.Current;

        var childBody = await WaitUntilAsync(
            async () =>
            {
                var (status, body) = await ExecuteResultAsync(
                    await AppUiEndpoints.ReadChild("treg", AgentAppScreen.ConversationSlotKey, proxies, brain, ct, caller, hosted, resolver: resolver));
                return status == 200 && body.Contains("Treg reply!", StringComparison.Ordinal) ? body : null;
            },
            Within,
            ct);

        Assert.Contains("Show open bugs", childBody, StringComparison.Ordinal);
        Assert.Contains("Treg reply!", childBody, StringComparison.Ordinal);

        // Path 2: AppUiEndpoints viewport, view, slot read, and /act.
        var (viewportStatus, viewportBody) = await ExecuteResultAsync(
            await AppUiEndpoints.ReadNode("viewport", $"{brain}/apps/treg", proxies, brain, ct, hosted: hosted, caller: caller, resolver: resolver));
        Assert.Equal(200, viewportStatus);
        Assert.Contains($"{brain}/apps/treg/view", viewportBody, StringComparison.Ordinal);

        var (viewStatus, viewBody) = await ExecuteResultAsync(
            await AppUiEndpoints.ReadNode("view", $"{brain}/apps/treg/view", proxies, brain, ct, hosted: hosted, caller: caller, resolver: resolver));
        Assert.Equal(200, viewStatus);
        Assert.Contains("Treg Assistant", viewBody, StringComparison.Ordinal);

        fixture.Model.Reply("Second reply via AppUiEndpoints!");
        var submitArgs = JsonSerializer.SerializeToElement(new { value = "Follow-up via Act" });
        var (actStatus, _) = await ExecuteResultAsync(
            await AppUiEndpoints.Act(
                new AppUiEndpoints.ActRequest($"{brain}/apps/treg", AgentAppScreen.ConversationSlotKey, "control.submitText", submitArgs),
                proxies,
                brain,
                ct,
                caller,
                hosted: hosted,
                resolver: resolver));
        Assert.Equal(200, actStatus);

        var updatedChildBody = await WaitUntilAsync(
            async () =>
            {
                var (status, body) = await ExecuteResultAsync(
                    await AppUiEndpoints.ReadChild("treg", AgentAppScreen.ConversationSlotKey, proxies, brain, ct, caller, hosted, resolver: resolver));
                return status == 200 && body.Contains("Second reply via AppUiEndpoints!", StringComparison.Ordinal) ? body : null;
            },
            Within,
            ct);

        Assert.Contains("Follow-up via Act", updatedChildBody, StringComparison.Ordinal);
        Assert.Contains("Second reply via AppUiEndpoints!", updatedChildBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsolatedToolSourcesExposesOnlyBoundSourceWithOverEightToolsAndAllowsIdempotentReseatWhileActive()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = fixture.NewBrain("tooliso");
        var brainId = new BrainId(brain);
        using var person = fixture.AsPerson(brain);

        AgentAppTestToolSourceNeuron.Clear();
        fixture.Model.ClearRequests();

        var keyA = $"{brain}/connection/{IAgentAppTestToolSource.TypeName}/source-a";
        var keyB = $"{brain}/connection/{IAgentAppTestToolSource.TypeName}/source-b";

        var toolsA = Enumerable.Range(1, 12)
            .Select(i => new ToolDescriptor(
                $"treg_op_{i:D2}",
                $"Treg Op {i:D2}",
                $"Performs treg operation {i:D2}.",
                """{"type":"object","properties":{"item":{"type":"string"}}}""",
                ReadOnly: true))
            .ToArray();
        var toolsB = new[]
        {
            new ToolDescriptor("secret_b_one", "Secret B 1", "Unbound source B tool.", """{"type":"object","properties":{}}""", ReadOnly: true),
            new ToolDescriptor("secret_b_two", "Secret B 2", "Unbound source B tool 2.", """{"type":"object","properties":{}}""", ReadOnly: true),
        };

        AgentAppTestToolSourceNeuron.Configure(keyA, new ToolSet("Source A", toolsA));
        AgentAppTestToolSourceNeuron.Configure(keyB, new ToolSet("Source B", toolsB));

        string[]? seenToolNames = null;
        var step = 0;
        fixture.Model.Respond((_, options, _) =>
        {
            if (step++ == 0)
            {
                seenToolNames = options?.Tools?.OfType<AIFunction>().Select(static f => f.Name).ToArray() ?? [];
                var firstTool = Assert.Single(seenToolNames, static n => n.EndsWith("treg_op_01", StringComparison.Ordinal));
                var call = new FunctionCallContent("call-1", firstTool, new Dictionary<string, object?> { ["item"] = "bug-42" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "All 12 treg tools verified.")));
        });

        var convId = AgentAppPolicy.ConversationOf("treg");
        var conversation = SiloNeurons.Get<IConversation>(fixture.Services, new NeuronId(brainId, IConversation.Type, IConversation.KeyOf(convId)));

        var definition = new AgentDefinition
        {
            DisplayName = "Treg",
            Description = "Scoped treg agent",
            Instructions = "Only use treg tools.",
            Contracts = [],
            IncludeComponentOperations = false,
            ToolSources = [keyA],
        };

        await conversation.Seat("agent-treg", definition);
        await conversation.Say("Inspect bug-42");

        var completedView = await WaitUntilAsync(
            async () =>
            {
                var view = await conversation.Read();
                return view.Active is null && view.Runs.Length >= 1 ? view : null;
            },
            Within,
            ct);

        Assert.Equal(AgentRunStatus.Completed, completedView.Runs[^1].Status);
        Assert.NotNull(seenToolNames);
        // All 12 tools from source-a exposed (bypassing 8-tool router cap), zero from source-b, zero meta/contract tools.
        Assert.Equal(12, seenToolNames.Length);
        Assert.All(seenToolNames, name => Assert.Contains("source_a_treg_op_", name, StringComparison.Ordinal));
        Assert.DoesNotContain(seenToolNames, name => name.Contains("source_b", StringComparison.Ordinal) || name.Contains("secret_b", StringComparison.Ordinal));
        Assert.DoesNotContain(seenToolNames, name => name.StartsWith("neurons_", StringComparison.Ordinal) || name is "find_tools" or "run_operation");

        // Tool invocation actually hit source-a and never source-b.
        var callRecord = Assert.Single(AgentAppTestToolSourceNeuron.Calls);
        Assert.Equal(keyA, callRecord.Key);
        Assert.Equal("treg_op_01", callRecord.Tool);

        // While a run is active: Seat with identical definition is a no-op; Seat with changed definition throws.
        var held = fixture.Model.Hold("Finished held run");
        await conversation.Say("Hold this turn");
        await held.Entered.WaitAsync(Within, ct);

        try
        {
            var identicalCopy = definition with { ToolSources = [keyA] };
            await conversation.Seat("agent-treg", identicalCopy);

            var changed = definition with { Instructions = "Different instructions." };
            await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.Seat("agent-treg", changed));
        }
        finally
        {
            held.Release();
        }

        await WaitUntilAsync(
            async () =>
            {
                var view = await conversation.Read();
                return view.Active is null && view.Runs.Length >= 2 ? view : null;
            },
            Within,
            ct);
    }

    private static Task<(int Status, string Body)> ExecuteResultAsync(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return Task.FromResult((status, JsonSerializer.Serialize(value, AppUiEndpoints.ViewJson)));
    }

    private static async Task<T> WaitUntilAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }
            await Task.Delay(40, ct);
        }

        throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds:0.#}s.");
    }
}
