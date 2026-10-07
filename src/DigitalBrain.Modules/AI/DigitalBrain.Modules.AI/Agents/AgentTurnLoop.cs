using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using DigitalBrain.AI.Agents.Context;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.AI.Agents;

// Everything one turn needs, settled by the agent before the loop starts.
internal sealed record TurnPlan(string ScopeId, string RunId, AiMessage[] Transcript, string? Summary, AiMessage Input, string Instructions,
    string[] ToolNames, string[] Contexts, ModelSelection? Model, InferenceOptions? Options, int MaxModelCalls, bool Streaming);

// Providers propose calls; this is the single, bounded tool invocation loop. It is pulled, not
// pushed: the consumer persists each event before asking for the next, so nothing external starts
// before the agent has written what came before it. A failure becomes the last event; Run never throws.
internal sealed class AgentTurnLoop(IServiceProvider services)
{
    public async IAsyncEnumerable<AgentTurnEvent> Run(TurnPlan plan, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var steps = Steps(plan, ct).GetAsyncEnumerator(ct);
        AgentTurnEvent.Failed? failure = null;
        while (true)
        {
            try { if (!await steps.MoveNextAsync()) { break; } }
            catch (Exception error)
            {
                // Only the run's own token makes a cancellation; a provider or tool timing out is a failure.
                var cancelled = error is OperationCanceledException && ct.IsCancellationRequested;
                failure = new(cancelled ? "The run was interrupted." : error.Message, cancelled);
                break;
            }
            yield return steps.Current;
        }
        if (failure is not null) { yield return failure; }
    }

    private async IAsyncEnumerable<AgentTurnEvent> Steps(TurnPlan plan, [EnumeratorCancellation] CancellationToken ct)
    {
        if (plan.MaxModelCalls is < 1 or > 128) { throw new ArgumentOutOfRangeException(nameof(plan), "MaxModelCalls must be between 1 and 128."); }
        if (plan.ToolNames.Distinct(StringComparer.Ordinal).Count() != plan.ToolNames.Length) { throw new ArgumentException("Tool names must be unique.", nameof(plan)); }
        var sessions = new List<IAgentToolSession>();
        try
        {
            yield return new AgentTurnEvent.Started(plan.RunId);
            string? currentCall = null;
            AgentToolContext Context() => new(plan.ScopeId, plan.RunId, currentCall ?? throw new InvalidOperationException("No active tool call."));
            var contexts = await ReadContexts(plan.Contexts, ct);
            var factoryTools = services.GetServices<IAgentToolFactory>().SelectMany(factory => factory.Create(Context)).ToList();
            // Context may name tools this host does not register; only registered ones join the turn.
            string[] selected = [.. plan.ToolNames, .. contexts.SelectMany(entry => entry.Tools)
                .Where(name => !plan.ToolNames.Contains(name) && factoryTools.Any(tool => tool.Name == name)).Distinct(StringComparer.Ordinal)];
            var tools = new List<AIFunction>();
            async Task AddTools(IReadOnlyList<string> names, AgentToolBinding? binding = null)
            {
                var pending = names.Where(name => tools.All(tool => tool.Name != name)).Distinct(StringComparer.Ordinal).ToArray();
                if (pending.Length == 0) { return; }
                var available = factoryTools.Where(tool => pending.Contains(tool.Name)).ToList();
                if (services.GetService<NativeTools>() is { } native) { available.AddRange(native.Resolve(pending)); }
                foreach (var source in services.GetServices<IAgentToolSource>())
                {
                    if (binding is not null && source.SourceId != binding.Source) { continue; }
                    // Session tools read the live call identity when invoked, after discovery.
                    var session = binding is null
                        ? await source.OpenAsync(pending, () => new(plan.ScopeId, plan.RunId, currentCall ?? ""), ct)
                        : await source.OpenBoundAsync(binding.Resource, pending, () => new(plan.ScopeId, plan.RunId, currentCall ?? ""), ct);
                    sessions.Add(session);
                    available.AddRange(session.Tools.Where(tool => pending.Contains(tool.Name)));
                }
                tools.AddRange(pending.Select(name =>
                {
                    var matches = available.Where(tool => tool.Name == name).ToArray();
                    return matches.Length == 1 ? matches[0]
                        : throw new InvalidOperationException($"Selected or offered tool '{name}' must have exactly one registration (found {matches.Length}).");
                }));
            }
            await AddTools(selected);
            var initialTools = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

            var catalog = services.GetRequiredService<ModelCatalog>();
            var selection = plan.Model;
            if (plan.Options?.Reasoning is not null || plan.Options?.MaxOutputTokens is not null)
            {
                selection = (selection ?? new()) with
                { Reasoning = plan.Options.Reasoning ?? selection?.Reasoning, MaxOutputTokens = plan.Options.MaxOutputTokens ?? selection?.MaxOutputTokens };
            }
            var settings = plan.Options is null ? null : plan.Options with { Reasoning = null };
            var resolved = await catalog.Resolve(selection, tools.Count > 0, ct);
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
                case AiProvider.Scripted:
                    // A scripted model plays its script; sampling and media declarations do not apply to it.
                    options = ModelCatalog.CreateOptions(resolved);
                    yield return new AgentTurnEvent.ModelSelected(resolved, catalog.Describe(resolved));
                    break;
                default:
                    var descriptor = catalog.Describe(resolved);
                    InferenceMapping.ValidateRequest(new InferenceRequest([.. plan.Transcript, plan.Input], selection, plan.Options), descriptor);
                    options = InferenceMapping.CreateOptions(resolved, settings, descriptor);
                    yield return new AgentTurnEvent.ModelSelected(resolved, descriptor);
                    break;
            }
            using var client = await catalog.CreateClient(resolved, ct);
            options.Instructions = plan.Instructions;
            options.Tools = [.. tools];
            var messages = new List<ChatMessage>();
            if (plan.Summary is { Length: > 0 }) { messages.Add(new(ChatRole.System, "Earlier conversation summary: " + plan.Summary)); }
            messages.AddRange(plan.Transcript.Select(InferenceMapping.ToChatMessage));
            foreach (var entry in contexts.Where(entry => entry.Text.Length > 0)) { messages.Add(new(ChatRole.System, entry.Text)); }
            messages.Add(InferenceMapping.ToChatMessage(plan.Input));
            var generated = new List<ChatMessage>();
            UsageDetails? usage = null;
            var calls = new HashSet<string>(StringComparer.Ordinal);
            for (var step = 0; step < plan.MaxModelCalls; step++)
            {
                ChatResponse response;
                if (plan.Streaming)
                {
                    var updates = new List<ChatResponseUpdate>();
                    await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
                    {
                        updates.Add(update);
                        if (!string.IsNullOrEmpty(update.Text)) { yield return new AgentTurnEvent.Text(update.Text); }
                    }
                    response = updates.ToChatResponse();
                }
                else
                {
                    response = await client.GetResponseAsync(messages, options, ct);
                    if (!string.IsNullOrEmpty(response.Text)) { yield return new AgentTurnEvent.Text(response.Text); }
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
                    var tool = tools.SingleOrDefault(item => item.Name == call.Name);
                    if (tool is null)
                    {
                        var unavailable = new
                        {
                            isError = true,
                            code = "tool_unavailable",
                            tool = call.Name,
                            message = "This tool is not available in this turn. Use an advertised discovery tool to find registered capabilities before retrying.",
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
                    try
                    {
                        result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()), ct);
                        ct.ThrowIfCancellationRequested();
                        if (result is AgentToolOffer offer)
                        {
                            if (offer.Tools.Count > 32) { throw new InvalidOperationException("A capability may offer at most 32 tools. Select a smaller operation group."); }
                            if (offer.ReplaceSelection) { tools.RemoveAll(item => !initialTools.Contains(item.Name)); }
                            if (tools.Where(item => !initialTools.Contains(item.Name)).Select(item => item.Name).Union(offer.Tools, StringComparer.Ordinal).Count() > 32)
                            { throw new InvalidOperationException("A turn may select at most 32 additional tools."); }
                            await AddTools(offer.Tools, offer.Binding);
                            options.Tools = [.. tools];
                            result = offer.Result;
                        }
                        serialized = JsonSerializer.Serialize(result);
                    }
                    catch (Exception error) { fault = ExceptionDispatchInfo.Capture(error); }
                    finally { currentCall = null; }
                    // An iterator cannot yield from a catch: the failure is reported here, then ends the turn as it failed.
                    if (fault is not null)
                    {
                        var cancelled = fault.SourceException is OperationCanceledException;
                        yield return new AgentTurnEvent.ToolFailed(call.CallId, call.Name, cancelled ? "tool_cancelled" : "tool_failed",
                            cancelled ? "The tool call was interrupted." : fault.SourceException.Message);
                        fault.Throw();
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
            foreach (var session in Enumerable.Reverse(sessions))
            {
                try { await session.DisposeAsync(); }
                catch (Exception error)
                {
                    // Cleanup must not replace the already recorded execution outcome.
                    services.GetService<ILoggerFactory>()?.CreateLogger<AgentTurnLoop>().LogWarning(error, "A tool session failed to close for run {RunId}.", plan.RunId);
                }
            }
        }
    }

    // A context that is missing or fails leaves the turn without it rather than failing the turn.
    private async Task<List<ContextEntry>> ReadContexts(string[] keys, CancellationToken ct)
    {
        if (keys.Length == 0) { return []; }
        var grains = services.GetRequiredService<IGrainFactory>();
        async Task<IEnumerable<ContextEntry>> Read(string key)
        {
            try { return (await grains.GetGrain<IAgentContext>(key).Read().WaitAsync(ct)).Entries; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger<AgentTurnLoop>().LogWarning(error, "Context {Context} could not be read; the turn continues without it.", key);
                return [];
            }
        }
        // The reads overlap; Task.WhenAll keeps the results in the order of keys.
        return [.. (await Task.WhenAll(keys.Select(Read))).SelectMany(entries => entries)];
    }
}
