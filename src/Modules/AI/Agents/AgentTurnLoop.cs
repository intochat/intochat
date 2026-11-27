using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Flutter.Workspace;
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
    public string[]? AlwaysOnOperationApps { get; init; }
    public string[]? OpenedOperationApps { get; init; }
    public string[]? ToolSources { get; init; }
}

// Providers propose calls; this is the single, bounded tool invocation loop. It is pulled, not
// pushed: the consumer persists each event before asking for the next, so nothing external starts
// before the agent has written what came before it. A failure becomes the last event; Run never throws.
internal sealed class AgentTurnLoop(
    IServiceProvider services,
    Func<string, CancellationToken, Task<ContextSnapshot>>? readContext = null,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>>? readOpenApps = null)
{
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
        var surface = services.GetService<NeuronSurface>();
        // B06: Callable tools come from the agent definition's declared contracts, scoped ToolSources, and installed components' operations; context entries never widen tools.
        string[] composed = surface is null
            ? []
            : [.. plan.Contracts.Select(surface.CurrentName).Distinct(StringComparer.Ordinal)];
        var contractTools = new List<ContractTool>();
        var routableTools = new List<AIFunction>();
        var allTools = new List<AIFunction>();
        var activeTools = new List<AITool>();
        NeuronSurface? restricted = null;
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
                Type[] contractTypes = [.. composed.Select(surface.FindContract)];
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

        DigitalBrain.Neurons.INeuronProxies? operationProxies = null;
        if (plan.IncludeComponentOperations && services.GetService<DigitalBrain.Neurons.INeuronProxies>() is { } proxies)
        {
            operationProxies = proxies;
            edge ??= services.GetService<DigitalBrain.Access.EdgeAuthority>();
            var brainId = DigitalBrain.Brains.BrainKey.BrainOf(plan.Conversation);
            var openApps = await ReadOpenApps(proxies, brainId, ct);
            var allowedApps = ToolPolicy.AppsForAssistant(plan.AlwaysOnOperationApps, openApps, plan.OpenedOperationApps);
            var operationTools = AgentTools.Operations(edge, proxies, plan.Conversation, allowedApps);
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
                    plan.ScopeId, ModelRole.Chat, selection, allTools.Count > 0, resolved.Model, unavailableReason ?? "offline", ct);
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

        var pump = new TurnEventPump();
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var additionalTools = new List<AITool>(allTools);
        var modelClient = new TurnModelClient(
            client,
            catalog,
            plan,
            selection,
            settings,
            resolved,
            allTools,
            activeTools,
            additionalTools,
            calls,
            pump,
            turn);
        client = null; // Ownership transferred to modelClient / invokingClient.

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = pumpCts.Token;

        var budgetExhausted = false;
        var runTask = Task.Run(async () =>
        {
            if (turn is not null) { Activity.Current = turn; }
            try
            {
                using var invokingClient = new ChatClientBuilder(modelClient)
                    .UseFunctionInvocation(services.GetService<ILoggerFactory>(), cfg =>
                    {
                        cfg.MaximumIterationsPerRequest = plan.MaxModelCalls;
                        cfg.MaximumConsecutiveErrorsPerRequest = 0;
                        cfg.AllowConcurrentInvocation = false;
                        cfg.AdditionalTools = additionalTools;
                        cfg.FunctionInvoker = async (context, invocationCt) =>
                        {
                            var call = context.CallContent;
                            // The consumer persists the started call before pulling the next event, so the tool never runs unrecorded.
                            await pump.EmitAndAwaitConsumedAsync(
                                new AgentTurnEvent.ToolStarted(call.CallId, call.Name, ToolJson.Serialize(call.Arguments)),
                                invocationCt).ConfigureAwait(false);

                            if (context.Function is UnavailableToolFunction)
                            {
                                var unavailable = new Dictionary<string, object?>
                                {
                                    ["isError"] = true,
                                    ["code"] = "tool_unavailable",
                                    ["tool"] = call.Name,
                                    ["message"] = "This tool is not available in this turn.",
                                };
                                await pump.EmitAndAwaitConsumedAsync(
                                    new AgentTurnEvent.ToolCompleted(call.CallId, call.Name, ToolJson.Serialize(unavailable)),
                                    invocationCt).ConfigureAwait(false);
                                if (modelClient.ModelCalls >= plan.MaxModelCalls)
                                {
                                    budgetExhausted = true;
                                    context.Terminate = true;
                                }
                                return unavailable;
                            }

                            currentCall = call.CallId;
                            object? result = null;
                            var serialized = "";
                            ExceptionDispatchInfo? fault = null;
                            var sensitive = SensitiveTelemetry;
                            var toolSpan = AgentTelemetry.Tool(call, sensitive, (context.Function as ContractTool)?.Schema);
                            try
                            {
                                result = await context.Function.InvokeAsync(context.Arguments, invocationCt).ConfigureAwait(false);
                                invocationCt.ThrowIfCancellationRequested();
                                serialized = ToolJson.Serialize(result);
                                AgentTelemetry.Result(toolSpan, serialized, sensitive);
                            }
                            catch (Exception error)
                            {
                                OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "agent tool invocation faulted before rethrow");
                                AgentTelemetry.Failed(toolSpan, error);
                                fault = ExceptionDispatchInfo.Capture(error);
                            }
                            finally
                            {
                                currentCall = null;
                                toolSpan?.Dispose();
                            }

                            if (fault is not null)
                            {
                                var cancelled = fault.SourceException is OperationCanceledException;
                                if (!cancelled) { fault.SourceException.Data["AgentToolFailed"] = true; }
                                await pump.EmitAsync(
                                    new AgentTurnEvent.ToolFailed(call.CallId, call.Name, cancelled ? "tool_cancelled" : "tool_failed",
                                        cancelled ? "The tool call was interrupted." : fault.SourceException.Message),
                                    CancellationToken.None).ConfigureAwait(false);
                                fault.Throw();
                            }

                            var targetOptions = context.Options ?? options;
                            if (string.Equals(context.Function.Name, AgentTools.SearchName, StringComparison.Ordinal))
                            {
                                WidenToolsFromSearch(result, serialized, routableTools, activeTools, targetOptions);
                                if (!ReferenceEquals(targetOptions, options))
                                {
                                    options.Tools = targetOptions.Tools;
                                    options.ToolMode = targetOptions.ToolMode;
                                }
                            }
                            else if (operationProxies is not null
                                && ToolPolicy.TryExtractLaunchedAppId(context.Function.Name, call.Arguments) is { } launchedAppId)
                            {
                                var beforeCount = allTools.Count;
                                WidenOperationsForLaunchedApp(launchedAppId, edge, operationProxies, plan.Conversation, routableTools, allTools, activeTools, targetOptions);
                                for (var i = beforeCount; i < allTools.Count; i++)
                                {
                                    additionalTools.Add(allTools[i]);
                                }
                                if (!ReferenceEquals(targetOptions, options))
                                {
                                    options.Tools = targetOptions.Tools;
                                    options.ToolMode = targetOptions.ToolMode;
                                }
                            }

                            await pump.EmitAndAwaitConsumedAsync(
                                new AgentTurnEvent.ToolCompleted(call.CallId, call.Name, serialized),
                                invocationCt).ConfigureAwait(false);
                            if (modelClient.ModelCalls >= plan.MaxModelCalls)
                            {
                                budgetExhausted = true;
                                context.Terminate = true;
                            }
                            return result;
                        };
                    })
                    .Build();

                ChatResponse response;
                if (plan.Streaming)
                {
                    var updates = new List<ChatResponseUpdate>();
                    await foreach (var update in invokingClient.GetStreamingResponseAsync(messages, options, runCt).ConfigureAwait(false))
                    {
                        updates.Add(update);
                    }
                    response = updates.ToChatResponse();
                }
                else
                {
                    response = await invokingClient.GetResponseAsync(messages, options, runCt).ConfigureAwait(false);
                }

                runCt.ThrowIfCancellationRequested();
                if (budgetExhausted || response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true)
                {
                    throw new InvalidOperationException("The agent reached its model-call budget.");
                }

                var usage = response.Usage;
                await pump.EmitAsync(
                    new AgentTurnEvent.Completed(
                        [.. response.Messages.Select(InferenceMapping.FromChatMessage)],
                        usage is null ? null : new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount)),
                    runCt).ConfigureAwait(false);
                await pump.EmitAsync(new AgentTurnEvent.Finished(), runCt).ConfigureAwait(false);
            }
            finally
            {
                pump.Complete();
            }
        }, CancellationToken.None);

        TaskCompletionSource? pendingAck = null;
        try
        {
            while (await pump.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (pump.Reader.TryRead(out var item))
                {
                    pendingAck = item.Ack;
                    yield return item.Event;
                    pendingAck?.TrySetResult();
                    pendingAck = null;
                }
            }

            await runTask.ConfigureAwait(false);
        }
        finally
        {
            pendingAck?.TrySetCanceled(CancellationToken.None);
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "agent turn background loop cleanup");
            }
        }
    }

    private sealed class TurnEventPump
    {
        private readonly Channel<(AgentTurnEvent Event, TaskCompletionSource? Ack)> _channel =
            Channel.CreateBounded<(AgentTurnEvent, TaskCompletionSource?)>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        public ChannelReader<(AgentTurnEvent Event, TaskCompletionSource? Ack)> Reader => _channel.Reader;

        public async Task EmitAsync(AgentTurnEvent ev, CancellationToken ct)
            => await _channel.Writer.WriteAsync((ev, null), ct).ConfigureAwait(false);

        public async Task EmitAndAwaitConsumedAsync(AgentTurnEvent ev, CancellationToken ct)
        {
            var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync((ev, ack), ct).ConfigureAwait(false);
            await ack.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void Complete() => _channel.Writer.TryComplete();
    }

    private sealed class UnavailableToolFunction(string name) : AIFunction
    {
        public override string Name => name;
        public override string Description => "Placeholder for an unavailable tool call.";

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }

    private sealed class TurnModelClient(
        IChatClient inner,
        ModelCatalog catalog,
        TurnPlan plan,
        ModelSelection? selection,
        InferenceOptions? settings,
        ResolvedModel resolved,
        List<AIFunction> allTools,
        List<AITool> activeTools,
        List<AITool> additionalTools,
        HashSet<string> seenCallIds,
        TurnEventPump pump,
        Activity? turn) : IChatClient
    {
        private IChatClient _inner = inner;
        private ResolvedModel _resolved = resolved;
        private int _step;
        private bool _firstTokenRecorded;
        private long _modelStart = Stopwatch.GetTimestamp();

        public int ModelCalls => _step;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var step = _step++;
            if (step == 0) { _modelStart = Stopwatch.GetTimestamp(); }

            string? deviceFallbackReason = null;
            ChatResponse response = null!;
            try
            {
                response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceUnavailableException unavailable) when (_resolved.Provider == AiProvider.Device && step == 0)
            {
                deviceFallbackReason = unavailable.Message;
            }

            if (deviceFallbackReason is not null)
            {
                options = await SwitchToCloudFallbackAsync(options, deviceFallbackReason, cancellationToken).ConfigureAwait(false);
                response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (response.Messages.Count == 0)
            {
                throw new InvalidOperationException("The provider returned no messages.");
            }

            if (!string.IsNullOrEmpty(response.Text))
            {
                if (!_firstTokenRecorded)
                {
                    _firstTokenRecorded = true;
                    AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(_modelStart));
                }
                await pump.EmitAsync(new AgentTurnEvent.TextChunk(response.Text), cancellationToken).ConfigureAwait(false);
            }

            ValidateAndPrepareCalls(response.Messages.SelectMany(static m => m.Contents).OfType<FunctionCallContent>());
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var step = _step++;
            if (step == 0) { _modelStart = Stopwatch.GetTimestamp(); }

            var updates = new List<ChatResponseUpdate>();
            var receivedFirstDelta = false;
            string? deviceFallbackReason = null;

            await using (var enumerator = _inner.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) { break; }
                        update = enumerator.Current;
                    }
                    catch (DeviceUnavailableException unavailable) when (_resolved.Provider == AiProvider.Device && step == 0 && !receivedFirstDelta)
                    {
                        deviceFallbackReason = unavailable.Message;
                        break;
                    }

                    updates.Add(update);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        receivedFirstDelta = true;
                        if (!_firstTokenRecorded)
                        {
                            _firstTokenRecorded = true;
                            AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(_modelStart));
                        }
                        await pump.EmitAsync(new AgentTurnEvent.TextChunk(update.Text), cancellationToken).ConfigureAwait(false);
                    }
                    else if (update.Contents.Any(static c => c is not UsageContent))
                    {
                        receivedFirstDelta = true;
                    }
                }
            }

            if (deviceFallbackReason is not null)
            {
                options = await SwitchToCloudFallbackAsync(options, deviceFallbackReason, cancellationToken).ConfigureAwait(false);
                updates.Clear();
                await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        if (!_firstTokenRecorded)
                        {
                            _firstTokenRecorded = true;
                            AgentTelemetry.FirstToken(turn ?? Activity.Current, Stopwatch.GetElapsedTime(_modelStart));
                        }
                        await pump.EmitAsync(new AgentTurnEvent.TextChunk(update.Text), cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = updates.ToChatResponse();
            if (snapshot.Messages.Count == 0)
            {
                throw new InvalidOperationException("The provider returned no messages.");
            }

            ValidateAndPrepareCalls(snapshot.Messages.SelectMany(static m => m.Contents).OfType<FunctionCallContent>());
            foreach (var update in updates)
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();

        private async Task<ChatOptions> SwitchToCloudFallbackAsync(
            ChatOptions? currentOptions,
            string reason,
            CancellationToken ct)
        {
            _inner.Dispose();
            _resolved = await catalog.ResolveCloudFallbackAsync(
                plan.ScopeId, ModelRole.Chat, selection, allTools.Count > 0, _resolved.Model, reason, ct).ConfigureAwait(false);
            var (rebuilt, descriptor) = BuildTurnOptions(catalog, plan, selection, settings, _resolved, activeTools);
            if (currentOptions is not null)
            {
                currentOptions.ModelId = rebuilt.ModelId;
                currentOptions.Temperature = rebuilt.Temperature;
                currentOptions.TopP = rebuilt.TopP;
                currentOptions.MaxOutputTokens = rebuilt.MaxOutputTokens;
                currentOptions.Reasoning = rebuilt.Reasoning;
                currentOptions.ResponseFormat = rebuilt.ResponseFormat;
                currentOptions.AdditionalProperties = rebuilt.AdditionalProperties;
                currentOptions.Instructions = rebuilt.Instructions;
                currentOptions.Tools = rebuilt.Tools;
                currentOptions.ToolMode = rebuilt.ToolMode;
                rebuilt = currentOptions;
            }

            await pump.EmitAndAwaitConsumedAsync(new AgentTurnEvent.ModelSelected(_resolved, descriptor), ct).ConfigureAwait(false);
            _inner = await catalog.CreateClient(plan.ScopeId, _resolved, ct).ConfigureAwait(false);
            return rebuilt;
        }

        private void ValidateAndPrepareCalls(IEnumerable<FunctionCallContent> functionCalls)
        {
            foreach (var call in functionCalls)
            {
                if (string.IsNullOrWhiteSpace(call.CallId) || !seenCallIds.Add(call.CallId))
                {
                    throw new InvalidOperationException("The model reused or omitted a tool call identity.");
                }

                if (!additionalTools.OfType<AIFunction>().Any(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal)))
                {
                    additionalTools.Add(new UnavailableToolFunction(call.Name));
                }
            }
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
        NeuronSurface restricted,
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

    private static void WidenOperationsForLaunchedApp(
        string launchedAppId,
        DigitalBrain.Access.EdgeAuthority? edge,
        DigitalBrain.Neurons.INeuronProxies proxies,
        string conversation,
        List<AIFunction> routableTools,
        List<AIFunction> allTools,
        List<AITool> activeTools,
        ChatOptions options)
    {
        var launchedOps = AgentTools.Operations(edge, proxies, conversation, [launchedAppId]);
        if (launchedOps.Count == 0) { return; }

        var allNames = allTools.Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
        var activeNames = activeTools.OfType<AIFunction>().Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
        var widened = false;
        foreach (var op in launchedOps)
        {
            if (allNames.Add(op.Name))
            {
                routableTools.Add(op);
                allTools.Add(op);
            }

            if (activeNames.Add(op.Name))
            {
                activeTools.Add(op);
                widened = true;
            }
        }

        if (widened)
        {
            options.Tools = [.. activeTools];
            options.ToolMode = ChatToolMode.Auto;
        }
    }

    private async Task<IReadOnlyList<string>> ReadOpenApps(
        DigitalBrain.Neurons.INeuronProxies proxies,
        string brainId,
        CancellationToken ct)
    {
        try
        {
            if (readOpenApps is not null)
            {
                return await readOpenApps(brainId, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(brainId))
            {
                return [];
            }

            var workspaceId = new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brainId), IWorkspace.Type, "main");
            var workspace = await proxies.Get<IWorkspace>(workspaceId).Read().WaitAsync(ct).ConfigureAwait(false);
            return ToolPolicy.OpenAppsFromWorkspace(workspace);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OsConsoleLog.For<AgentTurnLoop>().LogSwallowed(error, "reading open workspace apps for scoped component operations");
            return [];
        }
    }
}
