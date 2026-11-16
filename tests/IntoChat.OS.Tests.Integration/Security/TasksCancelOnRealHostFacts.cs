using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using EdgeKernelHarness = IntoChat.OS.Tests.Integration.Security.Gate20EdgeApprovalWallFacts.EdgeKernelHarness;

namespace IntoChat.OS.Tests.Integration.Security;

// The Tasks app's cancel on the product host, over HTTP, for a plan flow that waits for the person's approval.
// Tasks publishes one view, the board; the card of the selected task (TasksViewRenderer.RenderSide), which holds the Cancel
// button, is published by nothing. So no press of a "cancel" control can be sent today, by anyone. The cancel the button would
// send is the screen's one-way Request("tasks.cancel"); these facts send that, by the routes that reach it.
public sealed class TasksCancelOnRealHostFacts
{
    // What the flow's model answers at "draft"; the host has no provider, so the flow agent asks the host's chat client.
    private const string Plan = """{ "subtasks": [ { "title": "Write the notes", "type": "flow.note", "inner": {} } ] }""";

    private const string Refusal = "waits for a person at approve: only a person cancels it";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpAgentPressingTasksCancel_LeavesTheRunWaitingForThePerson()
    {
        await using var host = await Host();
        var (flowId, planId) = await PlanWaitingForApproval(host);

        // The person has Tasks open: open reads the flows onto the board.
        Accepted(await Send(host, "/apps/tasks/request", new { operation = "open", input = "{}" }, presence: true));

        using var mcpHttp = new HttpClient { BaseAddress = host.BaseAddress };
        mcpHttp.DefaultRequestHeaders.Add(PresenceProofValidator.HeaderName, host.PresenceToken);
        await using var mcp = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }, mcpHttp, ownsHttpClient: false),
            cancellationToken: Ct);

        var openKey = $"open:{flowId}";
        var board = await Eventually(() => ReadTasks(mcp), screen => screen.Controls.Contains(openKey), "Tasks never showed the run on its board");
        Assert.DoesNotContain("cancel", board.Controls);

        // The agent opens the run's card. The press arrives at Tasks, and the view still holds no cancel control.
        var selected = await Act(mcp, openKey);
        Assert.True(selected.GetProperty("accepted").GetBoolean(), selected.GetRawText());
        Assert.DoesNotContain("cancel", (await ReadTasks(mcp)).Controls);

        // It presses the key the card's Cancel button has. When Tasks publishes that card, this press reaches TasksApp.Cancel
        // and answers "Action accepted."; this fact then asserts the status line here instead.
        var pressed = await Act(mcp, "cancel");
        Assert.False(pressed.GetProperty("accepted").GetBoolean(), pressed.GetRawText());
        Assert.Equal("Action refused: UnknownNode", pressed.GetProperty("message").GetString());
        await StillWaitsForThePerson(host, flowId, planId);

        // What the button would send (ScreenAppEmitBridge: screen.Request("tasks.cancel")), stamped as McpCaller stamps an MCP
        // client. In process: no route gives an MCP client this request while the card is not published.
        using (Edge(host).EnterInProcess())
        using (RuntimeCallerContext.Stamp(McpCaller.AsAgent(Person(host))))
        {
            await TasksScreen(host).Request("tasks.cancel", JsonSerializer.SerializeToElement(new { flowId }));
        }

        var refused = await Eventually(() => ReadTasks(mcp), screen => screen.Texts.Any(text => text.Contains("Could not cancel", StringComparison.Ordinal)),
            "Tasks never said the cancel was refused");
        Assert.Contains(refused.Texts, text => text.Contains($"Could not cancel: flow {flowId} {Refusal}", StringComparison.Ordinal));
        await StillWaitsForThePerson(host, flowId, planId);
    }

    [Fact]
    public async Task UnprovedLocalProcessCancellingThroughTasks_LeavesTheRunWaitingForThePerson()
    {
        await using var host = await Host();
        var (flowId, planId) = await PlanWaitingForApproval(host);

        // No presence proof: fail-closed edge rejects unproved loopback calls with 401 (no "local" fallback).
        var unproved = await Send(host, "/apps/tasks/request", new { operation = "cancel", input = JsonSerializer.Serialize(new { flowId }) }, presence: false);
        Assert.Equal(HttpStatusCode.Unauthorized, unproved.Status);
        await StillWaitsForThePerson(host, flowId, planId);
    }

    [Fact]
    public async Task PersonPressingTasksCancel_CancelsTheRunThatWaitsForThem()
    {
        await using var host = await Host();
        var (flowId, planId) = await PlanWaitingForApproval(host);

        // The shell's route cannot carry the press: the published view has no node with the Cancel button's key.
        var press = await Send(host, "/apps/event", new { kind = "view", name = $"{host.Scope}/apps/tasks/view", key = "cancel", generation = 1 }, presence: true);
        Assert.True(press.Status == HttpStatusCode.BadRequest, $"{press.Status}: {press.Body}");
        await StillWaitsForThePerson(host, flowId, planId);

        // The person's cancel, with the presence proof, by the route that reaches the screen's Request today.
        Accepted(await Send(host, "/apps/tasks/request", new { operation = "cancel", input = JsonSerializer.Serialize(new { flowId }) }, presence: true));

        var ended = await Eventually(() => ReadFlow(host, flowId), view => view.Status != FlowStatus.Waiting, "the person's cancel did not end the run");
        Assert.Equal(FlowStatus.Cancelled, ended.Status);
        Assert.Equal("Cancelled from Tasks", ended.Detail);
        Assert.False(await Eventually(() => IsPending(host, planId), pending => !pending, "the plan stayed in the inbox"));
        Assert.DoesNotContain((await ReadTasks(host)).Texts, text => text.Contains("Could not cancel", StringComparison.Ordinal));
    }

    // The product composition with one host service replaced: the model. No provider is configured, whatever this machine's
    // environment holds, so the flow agent's turn goes to the host's chat client, which answers the plan.
    private static async Task<EdgeKernelHarness> Host()
    {
        var model = new SwappableChatModel();
        model.Reply(Plan);
        var host = await EdgeKernelHarness.StartAsync(Ct, builder =>
        {
            builder.Services.RemoveAll<IChatClient>();
            builder.Services.AddSingleton<IChatClient>(model);
            builder.Services.RemoveAll<IAiCredentials>();
            builder.Services.AddSingleton<IAiCredentials, NoProviders>();
            // EdgeKernelHarness defaults Topology=Local which sets Ollama.Endpoint and Default.Model;
            // clear them so ModelCatalog routes PlanFlow to the injected SwappableChatModel on any host.
            builder.Services.PostConfigure<AIOptions>(ai =>
            {
                ai.Ollama.Endpoint = null;
                ai.Default.Model = null;
            });
        });
        try
        {
            // The host installs the shipped apps after it starts; until Tasks is one, the apps route refuses a request to it.
            await Eventually(() => Get(host, "/apps/tasks"), response => response.Status == HttpStatusCode.OK, "the host never installed Tasks");
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static CapabilityCaller Person(EdgeKernelHarness host)
        => CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, host.Scope);

    // The person starts a plan flow; it drafts, then waits at "approve" with its plan pending in the inbox.
    private static async Task<(string FlowId, string PlanId)> PlanWaitingForApproval(EdgeKernelHarness host)
    {
        string flowId;
        using (Edge(host).EnterInProcess())
        using (RuntimeCallerContext.Stamp(Person(host)))
        {
            flowId = await Flows(host).Start(PlanFlow.Alias, "Notes", null, JsonSerializer.Serialize(new PlanInner { Goal = "Keep notes" }, PlanFlow.Json), Ct);
        }

        var waiting = await Eventually(() => ReadFlow(host, flowId),
            view => view is { Status: FlowStatus.Failed or FlowStatus.Cancelled or FlowStatus.Completed } or { Status: FlowStatus.Waiting, WaitingStep: "approve", ConfirmationPlanId: not null },
            "the plan flow did not reach its approval");
        Assert.Equal((FlowStatus.Waiting, WaitReason.Approval), (waiting.Status, waiting.WaitingFor));
        Assert.NotEmpty(((SwappableChatModel)host.Services.GetRequiredService<IChatClient>()).Requests);
        var planId = waiting.ConfirmationPlanId!;
        Assert.True(await Eventually(() => IsPending(host, planId), pending => pending, "the plan never reached the inbox"));
        return (flowId, planId);
    }

    private static async Task StillWaitsForThePerson(EdgeKernelHarness host, string flowId, string planId)
    {
        var view = await ReadFlow(host, flowId);
        Assert.Equal((FlowStatus.Waiting, "approve", WaitReason.Approval), (view.Status, view.WaitingStep, view.WaitingFor));
        using (Edge(host).EnterKernel(new BrainId(host.Scope)))
        {
            Assert.Equal(FlowStatus.Waiting, (await Proxies(host).Get<IFlowStarter>(Id(flowId)).Head()).Status);
        }
        Assert.True(await IsPending(host, planId));
    }

    private static EdgeAuthority Edge(EdgeKernelHarness host) => host.Services.GetRequiredService<EdgeAuthority>();

    private static INeuronProxies Proxies(EdgeKernelHarness host) => host.Services.GetRequiredService<INeuronProxies>();

    private static IFlows Flows(EdgeKernelHarness host) => Proxies(host).Get<IFlows>(IFlows.Key(host.Scope));

    private static IScreen TasksScreen(EdgeKernelHarness host) => Proxies(host).Get<IScreen>(new NeuronId(new BrainId(host.Scope), "app.tasks", "default"));

    private static NeuronId Id(string flowId)
    {
        Assert.True(NeuronId.TryParse(flowId, out var id));
        return id;
    }

    private static async Task<FlowView> ReadFlow(EdgeKernelHarness host, string flowId)
    {
        using var _ = Edge(host).EnterKernel(new BrainId(host.Scope));
        return await Flows(host).Read(flowId, Ct) ?? throw new InvalidOperationException($"The index has no flow {flowId}.");
    }

    private static async Task<bool> IsPending(EdgeKernelHarness host, string planId)
    {
        using var _ = Edge(host).EnterKernel(new BrainId(host.Scope));
        var inbox = await Proxies(host).Get<IConfirmationInbox>(IConfirmationInbox.Key(host.Scope)).Read();
        return inbox.Pending.Any(pending => string.Equals(pending.PlanId, planId, StringComparison.Ordinal));
    }

    private static async Task<(HttpStatusCode Status, string Body)> Send(EdgeKernelHarness host, string route, object body, bool presence)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/brains/{host.Scope}{route}") { Content = JsonContent.Create(body) };
        if (presence) { request.Headers.Add(PresenceProofValidator.HeaderName, host.PresenceToken); }
        using var response = await host.Client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static void Accepted((HttpStatusCode Status, string Body) response)
        => Assert.True(response.Status == HttpStatusCode.Accepted, $"{response.Status}: {response.Body}");

    private static async Task<(HttpStatusCode Status, string Body)> Get(EdgeKernelHarness host, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/brains/{host.Scope}{route}");
        request.Headers.Add(PresenceProofValidator.HeaderName, host.PresenceToken);
        using var response = await host.Client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    // The Tasks screen as the person's shell reads it.
    private static async Task<TasksScreenRead> ReadTasks(EdgeKernelHarness host)
    {
        var response = await Get(host, "/screens/tasks");
        Assert.True(response.Status == HttpStatusCode.OK, $"{response.Status}: {response.Body}");
        return TasksScreenRead.Of(response.Body);
    }

    // The Tasks screen as an MCP client reads it.
    private static async Task<TasksScreenRead> ReadTasks(McpClient mcp)
    {
        var result = await mcp.CallToolAsync("screen_read", new Dictionary<string, object?> { ["appId"] = "tasks" }, cancellationToken: Ct);
        Assert.True(result.IsError != true, Text(result));
        return TasksScreenRead.Of(Text(result));
    }

    private static async Task<JsonElement> Act(McpClient mcp, string key)
    {
        var result = await mcp.CallToolAsync("screen_act", new Dictionary<string, object?> { ["appId"] = "tasks", ["key"] = key }, cancellationToken: Ct);
        Assert.True(result.IsError != true, Text(result));
        return JsonSerializer.Deserialize<JsonElement>(Text(result));
    }

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, string otherwise)
    {
        var last = await read();
        for (var i = 0; i < 400 && !done(last); i++)
        {
            await Task.Delay(50, Ct);
            last = await read();
        }
        Assert.True(done(last), $"{otherwise}; last: {last}");
        return last;
    }

    // A screen snapshot's control keys and the values of its text nodes; the status line is one of them.
    private sealed record TasksScreenRead(string[] Controls, string[] Texts)
    {
        public static TasksScreenRead Of(string json)
        {
            using var snapshot = JsonDocument.Parse(json);
            List<string> texts = [];
            Collect(snapshot.RootElement.GetProperty("view"), texts);
            return new([.. snapshot.RootElement.GetProperty("controls").EnumerateArray().Select(control => control.GetProperty("key").GetString()!)], [.. texts]);
        }

        private static void Collect(JsonElement node, List<string> texts)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) { Collect(item, texts); }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "text"
                    && node.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    texts.Add(value.GetString()!);
                }
                foreach (var member in node.EnumerateObject()) { Collect(member.Value, texts); }
            }
        }

        public override string ToString() => $"controls [{string.Join(", ", Controls)}] texts [{string.Join(" | ", Texts)}]";
    }

    private sealed class NoProviders : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });

        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
