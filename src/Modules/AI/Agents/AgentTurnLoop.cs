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
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Web;
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
    public bool IncludeComponentOperations { get; init; }
    public string[]? ToolSources { get; init; }
}

// Providers propose calls; this is the single, bounded tool invocation loop. It is pulled, not
// pushed: the consumer persists each event before asking for the next, so nothing external starts
// before the agent has written what came before it. A failure becomes the last event; Run never throws.
internal sealed class AgentTurnLoop(IServiceProvider services, Func<string, CancellationToken, Task<ContextSnapshot>>? readContext = null)
{
    private static readonly string[] ContractAssemblyPrefixes = IntoChat.OS.Host.Surface.ContractAssemblies.PrefixArray();
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
        // B06: Callable tools come from the agent definition's declared contracts, scoped ToolSources, and installed components' operations; context entries never widen tools.
        string[] composed = surface is null
            ? []
            : [.. plan.Contracts.Select(static name => LoadedContracts.CurrentName(name, ContractAssemblyPrefixes)).Distinct(StringComparer.Ordinal)];
        var contractTools = new List<ContractTool>();
        var routableTools = new List<AIFunction>();
        var allTools = new List<AIFunction>();
        var activeTools = new List<AITool>();
        OsNeuronSurface? restricted = null;
        DigitalBrain.Access.EdgeAuthority? edge = null;
        IReadOnlyList<AIFunction> meta = [];
        if (composed.Length > 0 && surface is not null)
        {
            var hosted = (await surface.Contracts(ct)).ToHashSet(StringComparer.Ordinal);
            composed = [.. composed.Where(hosted.Contains)];
            if (composed.Length > 0)
            {
                restricted = await surface.Restrict(composed, ct);
                edge = services.GetService<DigitalBrain.Access.EdgeAuthority>();
                meta = AgentTools.Meta(edge, restricted, plan.Conversation);
                Type[] contractTypes = [.. composed.Select(static name => LoadedContracts.Find(name, ContractAssemblyPrefixes))];
                contractTools.AddRange(AgentTools.For(edge, restricted, contractTypes, plan.Conversation));
                routableTools.AddRange(contractTools);
                if (plan.ToolSources is null)
                {
                    var sourceTools = await ToolSourceTools.For(restricted, contractTypes, edge, plan.Conversation, ct);
                    routableTools.AddRange(sourceTools);
                }
                allTools.AddRange(meta);
                allTools.AddRange(routableTools);
            }
        }

        if (plan.ToolSources is { Length: > 0 } scopedKeys && surface is not null)
        {
            edge ??= services.GetService<DigitalBrain.Access.EdgeAuthority>();
            var scopedTools = await ToolSourceTools.ForKeys(surface, scopedKeys, edge, plan.Conversation, ct);
            routableTools.AddRange(scopedTools);
            allTools.AddRange(scopedTools);
        }

        if (plan.IncludeComponentOperations && services.GetService<DigitalBrain.Neurons.INeuronProxies>() is { } proxies)
        {
            edge ??= services.GetService<DigitalBrain.Access.EdgeAuthority>();
            var operationTools = AgentTools.Operations(edge, proxies, plan.Conversation);
            routableTools.AddRange(operationTools);
            allTools.AddRange(operationTools);
        }

        var catalog = services.GetRequiredService<ModelCatalog>();
        var (selection, settings) = InferenceMapping.Merge(plan.Model, plan.Options, overrides);
        var resolved = await catalog.Resolve(plan.ScopeId, selection, allTools.Count > 0, ct);
        IChatClient? client = null;
        if (resolved.Provider == AiProvider.Device)
        {
            var (deviceClient, unavailableReason) = await catalog.TryCreateDeviceRouteClientAsync(plan.ScopeId, resolved, ct);
            if (deviceClient is not null)
            {
                client = deviceClient;
            }
            else
            {
                resolved = await catalog.ResolveCloudFallbackAsync(
                    plan.ScopeId, Role.Chat, selection, allTools.Count > 0, resolved.Model, unavailableReason ?? "offline", ct);
            }
        }

        if (restricted is not null || routableTools.Count > 0)
        {
            var useNativeWebSearch = restricted is not null
                && resolved.Provider == AiProvider.Anthropic
                && composed.Contains(typeof(IWebSearch).FullName!, StringComparer.Ordinal)
                && await IsNativeWebSearchEnabledAsync(restricted, edge, plan.Conversation, ct);
            if (useNativeWebSearch)
            {
                contractTools.RemoveAll(IsWebSearchTool);
                routableTools.RemoveAll(IsWebSearchTool);
                allTools.RemoveAll(IsWebSearchTool);
            }

            IReadOnlyList<AIFunction> routed;
            if (routableTools.Count <= 16 || meta.Count == 0)
            {
                routed = routableTools;
            }
            else
            {
                var inputText = string.Join(" ", plan.Input.Content.OfType<AiText>().Select(static part => part.Text));
                var router = new ToolRouter(routableTools, ResolveEmbeddings(services));
                routed = await router.Route(inputText, 8, ct);
            }
            activeTools.AddRange(meta);
            activeTools.AddRange(routed);
            if (useNativeWebSearch)
            {
                activeTools.Add(new HostedWebSearchTool());
            }
        }

        var (options, selectedDescriptor) = BuildTurnOptions(catalog, plan, selection, settings, resolved, activeTools);
        yield return new AgentTurnEvent.ModelSelected(resolved, selectedDescriptor);
        client ??= await catalog.CreateClient(plan.ScopeId, resolved, ct);
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
        try
        {
            for (var step = 0; step < plan.MaxModelCalls; step++)
            {
                if (step == 0) { modelStart = Stopwatch.GetTimestamp(); }
                ChatResponse response;
                if (plan.Streaming)
                {
                    var updates = new List<ChatResponseUpdate>();
                    var receivedFirstDelta = false;
                    string? deviceFallbackReason = null;
                    await using (var enumerator = client.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct))
                    {
                        while (true)
                        {
                            ChatResponseUpdate update;
                            try
                            {
                                if (!await enumerator.MoveNextAsync()) { break; }
                                update = enumerator.Current;
                            }
                            catch (DeviceUnavailableException unavailable) when (resolved.Provider == AiProvider.Device && step == 0 && !receivedFirstDelta)
                            {
                                deviceFallbackReason = unavailable.Message;
                                break;
                            }

                            updates.Add(update);
                            if (!string.IsNullOrEmpty(update.Text))
                            {
                                receivedFirstDelta = true;
                                if (!firstTokenRecorded)
                                {
                                    firstTokenRecorded = true;
                                    AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(modelStart));
                                }
                                yield return new AgentTurnEvent.TextChunk(update.Text);
                            }
                            else if (update.Contents.Any(static c => c is not UsageContent))
                            {
                                receivedFirstDelta = true;
                            }
                        }
                    }

                    if (deviceFallbackReason is not null)
                    {
                        client.Dispose();
                        client = null;
                        resolved = await catalog.ResolveCloudFallbackAsync(
                            plan.ScopeId, Role.Chat, selection, allTools.Count > 0, resolved.Model, deviceFallbackReason, ct);
                        (options, selectedDescriptor) = BuildTurnOptions(catalog, plan, selection, settings, resolved, activeTools);
                        yield return new AgentTurnEvent.ModelSelected(resolved, selectedDescriptor);
                        client = await catalog.CreateClient(plan.ScopeId, resolved, ct);
                        updates.Clear();
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
                    }

                    response = updates.ToChatResponse();
                }
                else
                {
                    string? deviceFallbackReason = null;
                    response = null!;
                    try
                    {
                        response = await client.GetResponseAsync(messages, options, ct);
                    }
                    catch (DeviceUnavailableException unavailable) when (resolved.Provider == AiProvider.Device && step == 0)
                    {
                        deviceFallbackReason = unavailable.Message;
                    }

                    if (deviceFallbackReason is not null)
                    {
                        client.Dispose();
                        client = null;
                        resolved = await catalog.ResolveCloudFallbackAsync(
                            plan.ScopeId, Role.Chat, selection, allTools.Count > 0, resolved.Model, deviceFallbackReason, ct);
                        (options, selectedDescriptor) = BuildTurnOptions(catalog, plan, selection, settings, resolved, activeTools);
                        yield return new AgentTurnEvent.ModelSelected(resolved, selectedDescriptor);
                        client = await catalog.CreateClient(plan.ScopeId, resolved, ct);
                        response = await client.GetResponseAsync(messages, options, ct);
                    }

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
                        WidenToolsFromSearch(result, serialized, routableTools, activeTools, options);
                    }
                    var resultMessage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]);
                    messages.Add(resultMessage);
                    generated.Add(resultMessage);
                    yield return new AgentTurnEvent.ToolCompleted(call.CallId, call.Name, serialized);
                }
            }
            throw new InvalidOperationException("The agent reached its model-call budget.");
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static (ChatOptions Options, ModelDescriptor Descriptor) BuildTurnOptions(
        ModelCatalog catalog,
        TurnPlan plan,
        ModelSelection? selection,
        InferenceOptions? settings,
        ResolvedModel resolved,
        List<AITool> activeTools)
    {
        ModelDescriptor descriptor;
        ChatOptions options;
        if (resolved.Provider == AiProvider.Host)
        {
            // The deployment's own client: provider-neutral settings pass through, provider
            // settings it cannot honor fail here rather than being dropped.
            descriptor = new ModelDescriptor(resolved.Provider.ToString(), resolved.Model, null,
                resolved.Revision, CapabilitySupport.Supported, CapabilitySupport.Supported, CapabilitySupport.Supported,
                Temperature: CapabilitySupport.Supported, TopP: CapabilitySupport.Supported);
            options = InferenceMapping.CreateOptions(resolved, settings, descriptor);
        }
        else
        {
            descriptor = catalog.Describe(resolved);
            InferenceMapping.ValidateRequest(new InferenceRequest([.. plan.Transcript, plan.Input], selection, plan.Options), descriptor);
            options = InferenceMapping.CreateOptions(resolved, settings, descriptor);
        }

        options.Instructions = plan.Instructions;
        options.Tools = [.. activeTools];
        options.ToolMode = activeTools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto;
        return (options, descriptor);
    }

    internal static bool IsTrustedInterrupt(ContextEntry entry)
        => entry.EffectiveTags.Contains(ContextTags.Interrupt) && entry.Provenance?.Trust is ContextTrust.User or ContextTrust.Platform;

    private static IEmbeddingGenerator<string, Embedding<float>>? ResolveEmbeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static bool IsWebSearchTool(AIFunction tool)
        => tool is ContractTool contractTool
           && string.Equals(contractTool.Schema.Contract, typeof(IWebSearch).FullName, StringComparison.Ordinal)
           && string.Equals(contractTool.Schema.Method, nameof(IWebSearch.Search), StringComparison.Ordinal);

    private static async Task<bool> IsNativeWebSearchEnabledAsync(
        OsNeuronSurface restricted,
        DigitalBrain.Access.EdgeAuthority? edge,
        string conversation,
        CancellationToken ct)
    {
        var brain = edge?.Current?.Brain.Value ?? DigitalBrain.Brains.BrainKey.BrainOf(conversation);
        var key = string.IsNullOrWhiteSpace(brain) ? "{brain}/" + IWebSearch.DefaultKey : brain + "/" + IWebSearch.DefaultKey;
        try
        {
            var raw = await restricted.Call(typeof(IWebSearch).FullName!, key, nameof(IWebSearch.Provider), "{}", ct).ConfigureAwait(false);
            return raw is { } value && value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var code) => code == (int)SearchProvider.Native,
                JsonValueKind.String => Enum.TryParse<SearchProvider>(value.GetString(), ignoreCase: true, out var parsed) && parsed == SearchProvider.Native,
                _ => false,
            };
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "reading IWebSearch.Provider for native search decision");
            return false;
        }
    }

    private static void WidenToolsFromSearch(
        object? result,
        string serialized,
        IReadOnlyList<AIFunction> routableTools,
        List<AITool> activeTools,
        ChatOptions options)
    {
        var hitContracts = ExtractSearchHitContracts(result, serialized);
        if (hitContracts.Count == 0) { return; }

        var activeNames = activeTools.OfType<AIFunction>().Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
        var widened = false;
        foreach (var candidate in routableTools)
        {
            var contract = candidate switch
            {
                ContractTool contractTool => contractTool.Schema.Contract,
                ToolSourceTools.ToolSourceFunction sourceTool => sourceTool.ContractName,
                _ => null,
            };
            if (contract is not null && hitContracts.Contains(contract) && activeNames.Add(candidate.Name))
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
