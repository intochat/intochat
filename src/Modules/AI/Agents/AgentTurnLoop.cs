using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Modules.AI.Agents;

// Everything one turn needs, settled by the agent before the loop starts.
internal sealed record TurnPlan(string ScopeId, string Conversation, string RunId, AiMessage[] Transcript, string? Summary, AiMessage Input, string Instructions,
    string[] Contracts, string[] Contexts, ModelSelection? Model, InferenceOptions? Options, int MaxModelCalls, bool Streaming)
{
    public string? AgentName { get; init; }
}

// Providers propose calls; this is the single, bounded tool invocation loop. It is pulled, not
// pushed: the consumer persists each event before asking for the next, so nothing external starts
// before the agent has written what came before it. A failure becomes the last event; Run never throws.
internal sealed class AgentTurnLoop(IServiceProvider services, Func<string, CancellationToken, Task<ContextSnapshot>>? readContext = null)
{
    private static readonly string[] ContractAssemblyPrefixes = ["IntoChat.OS", "DigitalBrain"];
    public async IAsyncEnumerable<AgentTurnEvent> Run(TurnPlan plan, [EnumeratorCancellation] CancellationToken ct)
    {
        var telemetryConversation = plan.Conversation.StartsWith(plan.ScopeId + "/conversations/", StringComparison.Ordinal)
            ? plan.Conversation[(plan.ScopeId.Length + 1)..]
            : plan.Conversation;
        using var turn = AgentTelemetry.Turn(plan.AgentName ?? AgentName(plan.Conversation), telemetryConversation, plan.RunId);
        await using var steps = Steps(plan, turn, ct).GetAsyncEnumerator(ct);
        AgentTurnEvent.TurnFailed? failure = null;
        while (true)
        {
            // An async iterator does not carry Activity.Current between steps; each step runs under the turn so its spans nest.
            if (turn is not null) { System.Diagnostics.Activity.Current = turn; }
            try { if (!await steps.MoveNextAsync()) { break; } }
            catch (Exception error)
            {
                // Only the run's own token makes a cancellation; a provider or tool timing out is a failure.
                OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "agent turn step failed");
                var cancelled = error is OperationCanceledException && ct.IsCancellationRequested;
                failure = new(cancelled ? "The run was interrupted." : error.Message, cancelled)
                {
                    Failure = cancelled ? AgentFailure.Unknown : AgentFailures.Classify(error),
                };
                break;
            }
            yield return steps.Current;
        }
        if (failure is not null) { turn?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, failure.Message); yield return failure; }
    }

    // "{brain}/{app}/conversations/{id}" names the agent after its app; a key with no app segment names itself.
    internal static string AgentName(string conversation)
    {
        var parts = conversation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : conversation;
    }

    private bool SensitiveTelemetry => services.GetService<global::Microsoft.Extensions.Options.IOptions<AIOptions>>()?.Value.Telemetry.EnableSensitiveData ?? false;

    private async IAsyncEnumerable<AgentTurnEvent> Steps(TurnPlan plan, Activity? turn, [EnumeratorCancellation] CancellationToken ct)
    {
        if (plan.MaxModelCalls is < 1 or > 512) { throw new ArgumentOutOfRangeException(nameof(plan), "MaxModelCalls must be between 1 and 512."); }
        if (plan.Contracts.Distinct(StringComparer.Ordinal).Count() != plan.Contracts.Length) { throw new ArgumentException("Contracts must be unique.", nameof(plan)); }
        yield return new AgentTurnEvent.Started(plan.RunId);
        string? currentCall = null;
        var contexts = await ReadContexts(plan.Contexts, DigitalBrain.Brains.BrainKey.BrainOf(plan.Conversation), ct);
        // An interrupt from a user or the platform stops the turn before anything else is prepared; one written by an app or the model is not an order.
        if (contexts.FirstOrDefault(IsTrustedInterrupt) is { } interrupt)
        {
            yield return new AgentTurnEvent.TurnFailed("Blocked: " + interrupt.Text, false);
            yield break;
        }
        var overrides = contexts.Where(entry => entry.EffectiveTags.Contains(ContextTags.Inference)).Select(entry => InferenceOverride.Parse(entry.Text)).ToArray();
        var surface = services.GetService<OsNeuronSurface>();
        // B06: Callable tools come strictly from the agent definition's declared contracts; context entries never widen tools.
        string[] composed = surface is null
            ? []
            : [.. plan.Contracts.Select(static name => LoadedContracts.CurrentName(name, ContractAssemblyPrefixes)).Distinct(StringComparer.Ordinal)];
        var contractTools = new List<ContractTool>();
        var allTools = new List<AIFunction>();
        var activeTools = new List<AITool>();
        if (composed.Length > 0 && surface is not null)
        {
            var hosted = (await surface.Contracts(ct)).ToHashSet(StringComparer.Ordinal);
            composed = [.. composed.Where(hosted.Contains)];
            if (composed.Length > 0)
            {
                var restricted = await surface.Restrict(composed, ct);
                var edge = services.GetService<DigitalBrain.Access.EdgeAuthority>();
                var meta = AgentTools.Meta(edge, restricted, plan.Conversation);
                Type[] contractTypes = [.. composed.Select(static name => LoadedContracts.Find(name, ContractAssemblyPrefixes))];
                contractTools.AddRange(AgentTools.For(edge, restricted, contractTypes, plan.Conversation));
                allTools.AddRange(meta);
                allTools.AddRange(contractTools);

                var inputText = string.Join(" ", plan.Input.Content.OfType<AiText>().Select(static part => part.Text));
                var router = new ToolRouter(contractTools, ResolveEmbeddings(services));
                var routed = await router.Route(inputText, 8, ct);
                activeTools.AddRange(meta);
                activeTools.AddRange(routed);
            }
        }

        var catalog = services.GetRequiredService<ModelCatalog>();
        var (selection, settings) = InferenceMapping.Merge(plan.Model, plan.Options, overrides);
        var resolved = await catalog.Resolve(plan.ScopeId, selection, allTools.Count > 0, ct);
        ChatOptions options;
        switch (resolved.Provider)
        {
            case AiProvider.Host:
                // The deployment's own client: provider-neutral settings pass through, provider
                // settings it cannot honor fail here rather than being dropped.
                var hostDescriptor = new ModelDescriptor(resolved.Provider.ToString(), resolved.Model, null,
                    resolved.Revision, CapabilitySupport.Supported, CapabilitySupport.Supported, CapabilitySupport.Supported,
                    Temperature: CapabilitySupport.Supported, TopP: CapabilitySupport.Supported);
                options = InferenceMapping.CreateOptions(resolved, settings, hostDescriptor);
                yield return new AgentTurnEvent.ModelSelected(resolved, hostDescriptor);
                break;
            default:
                var descriptor = catalog.Describe(resolved);
                InferenceMapping.ValidateRequest(new InferenceRequest([.. plan.Transcript, plan.Input], selection, plan.Options), descriptor);
                options = InferenceMapping.CreateOptions(resolved, settings, descriptor);
                yield return new AgentTurnEvent.ModelSelected(resolved, descriptor);
                break;
        }
        using var client = await catalog.CreateClient(plan.ScopeId, resolved, ct);
        options.Instructions = plan.Instructions;
        options.Tools = [.. activeTools];
        options.ToolMode = activeTools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto;
        var messages = new List<ChatMessage>();
        // Only untagged entries are knowledge for the model; tagged ones are acted on above or belong to other consumers.
        foreach (var entry in contexts.Where(entry => entry.EffectiveTags.Length == 0 && entry.Text.Length > 0)) { messages.Add(new(ChatRole.System, entry.Text)); }
        messages.AddRange(plan.Transcript.Select(InferenceMapping.ToChatMessage));
        messages.Add(InferenceMapping.ToChatMessage(plan.Input));
        var generated = new List<ChatMessage>();
        UsageDetails? usage = null;
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var firstTokenRecorded = false;
        var modelStart = Stopwatch.GetTimestamp();
        for (var step = 0; step < plan.MaxModelCalls; step++)
        {
            if (step == 0) { modelStart = Stopwatch.GetTimestamp(); }
            ChatResponse response;
            if (plan.Streaming)
            {
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
                {
                    updates.Add(update);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!firstTokenRecorded)
                        {
                            firstTokenRecorded = true;
                            AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(modelStart));
                        }
                        yield return new AgentTurnEvent.TextChunk(update.Text);
                    }
                }
                response = updates.ToChatResponse();
            }
            else
            {
                response = await client.GetResponseAsync(messages, options, ct);
                if (!string.IsNullOrEmpty(response.Text))
                {
                    if (!firstTokenRecorded)
                    {
                        firstTokenRecorded = true;
                        AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(modelStart));
                    }
                    yield return new AgentTurnEvent.TextChunk(response.Text);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (response.Usage is not null) { (usage ??= new()).Add(response.Usage); }
            if (response.Messages.Count == 0) { throw new InvalidOperationException("The provider returned no messages."); }
            messages.AddRange(response.Messages);
            generated.AddRange(response.Messages);
            var requested = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            if (requested.Length == 0)
            {
                yield return new AgentTurnEvent.Completed([.. generated.Select(InferenceMapping.FromChatMessage)],
                    usage is null ? null : new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount));
                yield return new AgentTurnEvent.Finished();
                yield break;
            }
            foreach (var call in requested)
            {
                if (string.IsNullOrWhiteSpace(call.CallId) || !calls.Add(call.CallId)) { throw new InvalidOperationException("The model reused or omitted a tool call identity."); }
                // The consumer persists the started call before pulling the next event, so the tool never runs unrecorded.
                yield return new AgentTurnEvent.ToolStarted(call.CallId, call.Name, JsonSerializer.Serialize(call.Arguments));
                var tool = allTools.SingleOrDefault(item => item.Name == call.Name);
                if (tool is null)
                {
                    var unavailable = new
                    {
                        isError = true,
                        code = "tool_unavailable",
                        tool = call.Name,
                        message = "This tool is not available in this turn.",
                    };
                    var refusal = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, unavailable)]);
                    messages.Add(refusal);
                    generated.Add(refusal);
                    yield return new AgentTurnEvent.ToolCompleted(call.CallId, call.Name, JsonSerializer.Serialize(unavailable));
                    continue;
                }
                currentCall = call.CallId;
                object? result = null;
                var serialized = "";
                ExceptionDispatchInfo? fault = null;
                var sensitive = SensitiveTelemetry;
                var toolSpan = AgentTelemetry.Tool(call, sensitive, (tool as ContractTool)?.Schema);
                try
                {
                    result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()), ct);
                    ct.ThrowIfCancellationRequested();
                    serialized = JsonSerializer.Serialize(result);
                    AgentTelemetry.Result(toolSpan, serialized, sensitive);
                }
                catch (Exception error)
                {
                    OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "agent tool invocation faulted before rethrow");
                    AgentTelemetry.Failed(toolSpan, error);
                    fault = ExceptionDispatchInfo.Capture(error);
                }
                finally { currentCall = null; toolSpan?.Dispose(); }
                // An iterator cannot yield from a catch: the failure is reported here, then ends the turn as it failed.
                if (fault is not null)
                {
                    var cancelled = fault.SourceException is OperationCanceledException;
                    if (!cancelled) { fault.SourceException.Data["AgentToolFailed"] = true; }
                    yield return new AgentTurnEvent.ToolFailed(call.CallId, call.Name, cancelled ? "tool_cancelled" : "tool_failed",
                        cancelled ? "The tool call was interrupted." : fault.SourceException.Message);
                    fault.Throw();
                }
                if (string.Equals(tool.Name, AgentTools.SearchName, StringComparison.Ordinal))
                {
                    WidenToolsFromSearch(result, serialized, contractTools, activeTools, options);
                }
                var resultMessage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]);
                messages.Add(resultMessage);
                generated.Add(resultMessage);
                yield return new AgentTurnEvent.ToolCompleted(call.CallId, call.Name, serialized);
            }
        }
        throw new InvalidOperationException("The agent reached its model-call budget.");
    }

    internal static bool IsTrustedInterrupt(ContextEntry entry)
        => entry.EffectiveTags.Contains(ContextTags.Interrupt) && entry.Provenance?.Trust is ContextTrust.User or ContextTrust.Platform;

    private static IEmbeddingGenerator<string, Embedding<float>>? ResolveEmbeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static void WidenToolsFromSearch(
        object? result,
        string serialized,
        List<ContractTool> contractTools,
        List<AITool> activeTools,
        ChatOptions options)
    {
        var hitContracts = ExtractSearchHitContracts(result, serialized);
        if (hitContracts.Count == 0) { return; }

        var activeNames = activeTools.OfType<AIFunction>().Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
        var widened = false;
        foreach (var candidate in contractTools)
        {
            if (hitContracts.Contains(candidate.Schema.Contract) && activeNames.Add(candidate.Name))
            {
                activeTools.Add(candidate);
                widened = true;
            }
        }
        if (widened)
        {
            options.Tools = [.. activeTools];
            options.ToolMode = ChatToolMode.Auto;
        }
    }

    private static HashSet<string> ExtractSearchHitContracts(object? result, string serialized)
    {
        var contracts = new HashSet<string>(StringComparer.Ordinal);
        if (result is IEnumerable<NeuronTypeHit> hits)
        {
            foreach (var hit in hits)
            {
                if (!string.IsNullOrWhiteSpace(hit.Type.Contract)) { contracts.Add(hit.Type.Contract); }
            }
            return contracts;
        }

        if (result is JsonElement element)
        {
            CollectContractsFromJson(element, contracts);
            return contracts;
        }

        if (!string.IsNullOrWhiteSpace(serialized))
        {
            try
            {
                using var doc = JsonDocument.Parse(serialized);
                CollectContractsFromJson(doc.RootElement, contracts);
            }
            catch (JsonException error)
            {
                OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "parsing neurons_search result for tool widening");
            }
        }
        return contracts;
    }

    private static void CollectContractsFromJson(JsonElement root, HashSet<string> contracts)
    {
        if (root.ValueKind != JsonValueKind.Array) { return; }
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            if ((item.TryGetProperty("type", out var typeEl) || item.TryGetProperty("Type", out typeEl))
                && typeEl.ValueKind == JsonValueKind.Object
                && (typeEl.TryGetProperty("contract", out var nested) || typeEl.TryGetProperty("Contract", out nested))
                && nested.ValueKind == JsonValueKind.String
                && nested.GetString() is { Length: > 0 } nestedName)
            {
                contracts.Add(nestedName);
                continue;
            }
            if ((item.TryGetProperty("contract", out var direct) || item.TryGetProperty("Contract", out direct))
                && direct.ValueKind == JsonValueKind.String
                && direct.GetString() is { Length: > 0 } directName)
            {
                contracts.Add(directName);
            }
        }
    }

    // A context that is missing or fails leaves the turn without it rather than failing the turn.
    // A key that starts with "context/" is relative to the agent's own brain.
    internal static string Absolute(string brain, string key)
        => key.StartsWith("context/", StringComparison.Ordinal) ? ContextKeys.Of(brain, key["context/".Length..]) : key;

    private async Task<List<ContextEntry>> ReadContexts(string[] keys, string brain, CancellationToken ct)
    {
        if (keys.Length == 0) { return []; }
        // A context is a plain neuron (06-05), reached by its os key "{brain}/{key}".
        async Task<IEnumerable<ContextEntry>> Read(string key)
        {
            try
            {
                var snapshot = readContext is not null
                    ? await readContext(key, ct)
                    : await SiloNeurons.Get<IAgentContext>(services, key).Read().WaitAsync(ct);
                return snapshot.Entries;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<AgentTurnLoop>().LogWarning(error, "Context {Context} could not be read; the turn continues without it.", key);
                return [];
            }
        }
        // The reads overlap; Task.WhenAll keeps the results in the order of keys.
        return [.. (await Task.WhenAll(keys.Select(key => Read(Absolute(brain, key))))).SelectMany(entries => entries)];
    }
}
