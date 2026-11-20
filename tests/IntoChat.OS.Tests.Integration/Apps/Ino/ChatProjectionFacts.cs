using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;
using Microsoft.Extensions.AI;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ShellChatRole = IntoChat.OS.Modules.Flutter.Chat.ChatRole;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

public sealed class ChatProjectionFacts(InoSiloFixture fixture) : IClassFixture<InoSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AChatThatSelectsAnAgentShowsItsRun()
    {
        var brain = fixture.NewBrain("chatproj");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.Reply("Hi Vlad.");

        var agentKey = $"{brain}/ino/conversations/c1";
        var chatKey = $"{brain}/ino/chats/c1";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        var chat = SiloNeurons.Get<IChat>(fixture.Services, chatKey);

        await chat.SelectAgent(agentKey, 0, Guid.NewGuid());
        await agent.Start(AgentTurn.Text("hello"));

        var projected = await WaitUntilChatAsync(
            chat,
            s => s.Run?.Status == ChatRunStatus.Completed && (s.Projection?.Messages.Length ?? 0) >= 2);

        Assert.Equal([ShellChatRole.User, ShellChatRole.Assistant], projected.Projection!.Messages.Select(m => m.Role));
        Assert.Equal(["hello", "Hi Vlad."], projected.Projection.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task AFailedRunProjectsFailedStatusAndAgentPreservesFailureKind()
    {
        var brain = fixture.NewBrain("chatfail");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.Fail(new HttpRequestException("upstream 503", null, HttpStatusCode.ServiceUnavailable));

        var agentKey = $"{brain}/ino/conversations/c2";
        var chatKey = $"{brain}/ino/chats/c2";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        var chat = SiloNeurons.Get<IChat>(fixture.Services, chatKey);

        await chat.SelectAgent(agentKey, 0, Guid.NewGuid());
        var runId = await agent.Start(AgentTurn.Text("hello"));

        var projected = await WaitUntilChatAsync(
            chat,
            s => s.Run?.Status == ChatRunStatus.Failed);

        Assert.Equal(ChatRunStatus.Failed, projected.Run!.Status);
        var agentState = await agent.Read();
        var run = Assert.Single(agentState.Runs, r => r.RunId == runId);
        Assert.Equal(AgentFailure.ProviderUnavailable, run.Failure);
    }

    [Fact]
    public async Task AFailedRunCarriesItsCodeThroughTheChatProjection()
    {
        var brain = fixture.NewBrain("chatfailcode");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.Fail(new HttpRequestException("upstream 503", null, HttpStatusCode.ServiceUnavailable));

        var agentKey = $"{brain}/ino/conversations/c2code";
        var chatKey = $"{brain}/ino/chats/c2code";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        var chat = SiloNeurons.Get<IChat>(fixture.Services, chatKey);

        await chat.SelectAgent(agentKey, 0, Guid.NewGuid());
        await agent.Start(AgentTurn.Text("hello"));

        var projected = await WaitUntilChatAsync(
            chat,
            s => s.Run?.Status == ChatRunStatus.Failed
                && (s.Projection?.Messages.Any(m => m.Role == ShellChatRole.Assistant && m.Status == "failed") ?? false));

        var assistant = projected.Projection!.Messages.Last(m => m.Role == ShellChatRole.Assistant);
        Assert.Equal("provider-unavailable", assistant.Failure);
    }

    [Fact]
    public async Task B06_UntrustedContextEntryContractsNeverWidenCallableToolsAndRuleViolationReturnsStructuredToolError()
    {
        var brain = fixture.NewBrain("b06sec");
        using var _ = fixture.AsPerson(brain);

        // 1. Prove ContractActions.Marked catches DigitalBrain.Rules.RuleViolationException and returns { isError = true, message }
        var violationResult = await ContractActions.Marked(
            fixture.Edge,
            $"{brain}/ino/conversations/b06",
            () => throw new RuleViolationException(new Rejected("PromoteNote", ["owner-only"], ["Only a person may promote a memory note."])));

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(violationResult)))
        {
            Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
            Assert.Contains("Only a person may promote", doc.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        // 2. Write a context entry attempting to inject IUserSettings into Contracts while the agent only declares IMemory!
        var injectedContract = typeof(IUserSettings).FullName!;
        var allowedContract = typeof(IMemory).FullName!;
        var ctxId = new NeuronId(new BrainId(brain), "agent-context", "context/untrusted.inject");
        await SiloNeurons.Get<IAgentContext>(fixture.Services, ctxId)
            .Set(new ContextEntry("untrusted", "Context note", [injectedContract]));

        var callAttempted = 0;
        fixture.Model.Respond((messages, options, _) =>
        {
            var step = Interlocked.Increment(ref callAttempted);
            if (step == 1)
            {
                // The LLM tries to call the injected IUserSettings contract that was NOT in AgentDefinition.Contracts!
                var callArgs = new Dictionary<string, object?>
                {
                    ["contract"] = injectedContract,
                    ["key"] = $"{brain}/settings",
                    ["method"] = nameof(IUserSettings.Read),
                    ["arguments"] = "[]",
                };
                var contents = new List<AIContent>
                {
                    new FunctionCallContent("call-1", NeuronSurfaceTools.CallName, callArgs),
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(AiChatRole.Assistant, contents)));
            }

            var toolResult = messages
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => JsonSerializer.Serialize(r.Result))
                .FirstOrDefault() ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(AiChatRole.Assistant, $"Tool outcome: {toolResult}")));
        });

        var agentKey = $"{brain}/ino/conversations/b06";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        await agent.Configure(
            new AgentDefinition
            {
                Contracts = [allowedContract],
                Contexts = ["context/untrusted.inject"],
            },
            0);

        var runId = await agent.Start(AgentTurn.Text("Try reading settings"));
        var finished = await WaitUntilAgentAsync(agent, s => s.Runs.Any(r => r.RunId == runId));
        var completedRun = Assert.Single(finished.Runs, r => r.RunId == runId);

        Assert.Equal(AgentRunStatus.Completed, completedRun.Status);
        var recordedToolCall = Assert.Single(completedRun.ToolCalls);
        Assert.NotNull(recordedToolCall.Error);
        Assert.Contains("not a contract this app composes", recordedToolCall.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NeuronsCallAcceptsNativeJsonArrayAndOptionalDefaultParametersOnIMemory()
    {
        var brain = fixture.NewBrain("memargs");
        using var _ = fixture.AsPerson(brain);
        var memoryContract = typeof(IMemory).FullName!;

        var stepCount = 0;
        fixture.Model.Respond((_, _, _) =>
        {
            if (Interlocked.Increment(ref stepCount) == 1)
            {
                var nativeArrayArgs = JsonDocument.Parse("""["owner","testing","Always use xUnit v3"]""").RootElement.Clone();
                var callArgs = new Dictionary<string, object?>
                {
                    ["contract"] = memoryContract,
                    ["key"] = $"{brain}/memory",
                    ["method"] = nameof(IntoChat.OS.Modules.AI.Memory.IMemory.RememberPreference),
                    ["arguments"] = nativeArrayArgs,
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    AiChatRole.Assistant,
                    [new FunctionCallContent("call-mem", NeuronSurfaceTools.CallName, callArgs)])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(AiChatRole.Assistant, "Saved preference.")));
        });

        var agentKey = $"{brain}/ino/conversations/mem-default-args";
        var agent = SiloNeurons.Get<IAgent>(fixture.Services, agentKey);
        await agent.Configure(new AgentDefinition { Contracts = [memoryContract] }, 0);

        var runId = await agent.Start(AgentTurn.Text("Remember I prefer xUnit v3"));
        var finished = await WaitUntilAgentAsync(agent, s => s.Runs.Any(r => r.RunId == runId));
        var completedRun = Assert.Single(finished.Runs, r => r.RunId == runId);

        Assert.Equal(AgentRunStatus.Completed, completedRun.Status);
        var toolCall = Assert.Single(completedRun.ToolCalls);
        Assert.Null(toolCall.Error);

        var snapshot = await SiloNeurons.Get<IntoChat.OS.Modules.AI.Memory.IMemory>(fixture.Services, $"{brain}/memory").Read();
        Assert.Contains(snapshot.Notes, n => n.Scope == "owner" && n.Subject == "testing" && n.Body == "Always use xUnit v3" && n.Importance == 8);
    }

    private static async Task<ChatState> WaitUntilChatAsync(IChat chat, Func<ChatState, bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        ChatState last = default!;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = await chat.Read();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(35, Ct);
        }
        throw new TimeoutException($"Timed out waiting for ChatState. Last revision={last?.Revision}, status={last?.Run?.Status}");
    }

    private static async Task<AgentState> WaitUntilAgentAsync(IAgent agent, Func<AgentState, bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        AgentState last = default!;
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = await agent.Read();
            if (predicate(last))
            {
                return last;
            }
            await Task.Delay(35, Ct);
        }
        throw new TimeoutException("Timed out waiting for AgentState.");
    }
}
