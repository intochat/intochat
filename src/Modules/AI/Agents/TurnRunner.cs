using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Agents;

// The run as one turn streams: the owner reads it after the stream ends (its finally decides the outcome).
internal sealed class TurnState(AgentRun run)
{
    public AgentRun Run { get; set; } = run;
    public string Visible { get; set; } = "";   // masked text so far, tail held back until final
    public bool Completed { get; set; }
    public AgentTurnEvent.TurnFailed? Failure { get; set; }
    internal StringBuilder Raw { get; } = new();
    public AgentRun Streamed => Visible.Length > 0 ? Run with { Reply = Visible } : Run;
}

internal sealed class TurnSink(Func<AgentRun, Task> store)
{
    // The run so far; called for the first words, then at most once per TurnRunner.ProgressInterval, at model selection,
    // at each tool step, and when the provider stalls with unstored text. A throw on a stall store is logged, not fatal.
    public Task Store(AgentRun run) => store(run);
}

internal sealed class TurnRunner(
    INeuronContext context,
    NeuronId owner,
    ISecretRedactor redactor,
    TimeProvider clock,
    IServiceProvider services,
    ILogger logger)
{
    // Every stored progress step writes the whole record and journals a signal, so streamed
    // text is stored and announced at most once per interval; the first words go at once, and a
    // turn's end, a tool step or a stalled provider take the rest.
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    // Replayed history is bounded by size as well as count: tool results (capability listings,
    // table queries) would otherwise grow every turn until the provider refuses the context.
    internal const int MaxHistoryCharacters = 120_000;
    internal const int MaxStoredToolResultCharacters = 4_000;

    private string OwnerKey => owner.FullKey();

    public async Task<string> Mask(string text) => (await redactor.Redact(text, CancellationToken.None))!;

    public async Task<AiMessage> Mask(AiMessage message) => new(message.Role, [.. await MaskAll(message.Content)]);

    private async Task<AiContent[]> MaskAll(IReadOnlyList<AiContent> contents)
    {
        var masked = new List<AiContent>(contents.Count);
        foreach (var content in contents)
        {
            masked.Add(content switch
            {
                AiText text => text with { Text = await Mask(text.Text) },
                AiReasoning reasoning => reasoning with { Text = await Mask(reasoning.Text) },
                AiToolCall call => call with { ArgumentsJson = await Mask(call.ArgumentsJson) },
                AiToolResult result => result with { ResultJson = await Mask(result.ResultJson) },
                _ => content,
            });
        }
        return [.. masked];
    }

    // Masks the streamed text; until it is complete, a tail that could still grow into a secret is held back.
    public async Task Refresh(TurnState turn, bool final)
    {
        var raw = turn.Raw.ToString();
        var tail = final ? 0 : Math.Min(raw.Length, await redactor.TailLength(CancellationToken.None));
        turn.Visible = await Mask(raw[..(raw.Length - tail)]);
    }

    // A context is listed by its os key "{brain}/{key}"; one in another brain is not followed.
    public NeuronId[] ContextIds(string[] keys)
    {
        var prefix = owner.Brain.Value + "/";
        return [.. keys.Select(key => AgentTurnLoop.Absolute(owner.Brain.Value, key))
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(key => new NeuronId(owner.Brain, "agent-context", key[prefix.Length..]))];
    }

    public Task<ContextSnapshot> ReadContext(string key, CancellationToken ct)
    {
        var prefix = owner.Brain.Value + "/";
        var absolute = AgentTurnLoop.Absolute(owner.Brain.Value, key);
        if (!absolute.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(new ContextSnapshot(0, []));
        }
        return context.Get<IAgentContext>(new NeuronId(owner.Brain, "agent-context", absolute[prefix.Length..])).Read().WaitAsync(ct);
    }

    public async Task<ContextEntry?> FirstInterrupt(string[] contexts)
    {
        foreach (var id in ContextIds(contexts))
        {
            ContextSnapshot snapshot;
            try { snapshot = await context.Get<IAgentContext>(id).Read(); }
            catch (Exception error)
            {
                logger.LogWarning(error, "Context {Context} could not be read; it does not block the agent.", id);
                continue;
            }
            if (snapshot.Entries.FirstOrDefault(AgentTurnLoop.IsTrustedInterrupt) is { } interrupt) { return interrupt; }
        }
        return null;
    }

    // The contexts a definition lists under the given name; the producers below write only where asked for it.
    private NeuronId[] ContextsNamed(string[] keys, string name)
        => ContextIds([.. keys.Select(key => AgentTurnLoop.Absolute(owner.Brain.Value, key)).Where(key => key.EndsWith("/context/" + name, StringComparison.Ordinal))]);

    // Recall and structured memory/UI context slices run before the turn is planned, so the loop's read sees what they wrote.
    public async Task Hydrate(string[] contexts, string query)
    {
        await WriteRecall(contexts, query);
        var activeProjectBrief = await WriteSessionReturning(contexts);
        await WriteWorkingMemory(contexts, activeProjectBrief);
        await WriteUiState(contexts);
    }

    // The summary is an ordinary entry once a conversation.summary context is listed; the loop reads it like any other.
    public async Task WriteSummary(string[] contexts, string summary)
    {
        foreach (var id in ContextsNamed(contexts, "conversation.summary"))
        {
            try { await context.Get<IAgentContext>(id).Set(new("summary", "Earlier conversation summary: " + summary, [])); }
            catch (Exception error) { logger.LogWarning(error, "The summary could not be written to context {Context}.", id); }
        }
    }

    public async Task RecordMilestone(string[] contexts, string? userText, string? replyText)
    {
        if (ContextsNamed(contexts, "session.journal").Length == 0) { return; }
        try
        {
            var milestone = SessionJournalNeuron.ExtractMilestoneFromTurn(OwnerKey, userText, replyText);
            await context.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(owner.Brain)).RecordMilestone(milestone);
        }
        catch (Exception error) { logger.LogWarning(error, "Session journal milestone could not be recorded."); }
    }

    private async Task WriteRecall(string[] keys, string query)
    {
        var ids = ContextsNamed(keys, "memory.recall");
        if (ids.Length == 0) { return; }
        try
        {
            var pack = await context.Get<IMemory>(new NeuronId(owner.Brain, "ai.memory", "memory")).RecallPack(query, null, 5);
            foreach (var id in ids)
            {
                var ctx = context.Get<IAgentContext>(id);
                if (pack.OwnerRules.Length == 0)
                {
                    await ctx.Remove("owner-rules");
                }
                else
                {
                    var rulesText = "Owner rules and preferences:\n"
                        + string.Join("\n", pack.OwnerRules.Select(rule => $"- [{rule.Scope}] {rule.Subject}: {rule.Body}"));
                    await ctx.Set(new("owner-rules", rulesText, []));
                }

                if (pack.RecalledItems.Length == 0)
                {
                    await ctx.Remove("recall");
                }
                else
                {
                    await ctx.Set(new("recall", string.Join("\n", pack.RecalledItems.Select(hit => "- " + hit.Text)), []));
                }
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Memory recall could not be written; the turn runs without it."); }
    }

    private async Task WriteWorkingMemory(string[] keys, string? journalProjectBrief)
    {
        var ids = ContextsNamed(keys, "working.memory");
        if (ids.Length == 0) { return; }
        try
        {
            var workingMemory = context.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(owner.Brain));
            var pack = await workingMemory.FreezeSession(OwnerKey);
            var briefPointer = pack.Project ?? journalProjectBrief;
            ContextBrief? activeBrief = null;
            if (!string.IsNullOrWhiteSpace(briefPointer))
            {
                activeBrief = await workingMemory.GetBrief(briefPointer.Trim());
            }

            foreach (var id in ids)
            {
                var ctx = context.Get<IAgentContext>(id);
                if (string.IsNullOrWhiteSpace(pack.FormattedPrompt))
                {
                    await ctx.Remove("startup");
                }
                else
                {
                    await ctx.Set(new("startup", pack.FormattedPrompt, [typeof(IWorkingMemory).FullName!]));
                }

                if (activeBrief is null)
                {
                    await ctx.Remove("project-brief");
                }
                else
                {
                    await ctx.Set(new("project-brief", $"[Active Brief: {activeBrief.Title}]\n{activeBrief.Content}", [typeof(IWorkingMemory).FullName!]));
                }
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Working memory context could not be written; the turn runs without it."); }
    }

    private async Task<string?> WriteSessionReturning(string[] keys)
    {
        var ids = ContextsNamed(keys, "session.journal");
        if (ids.Length == 0) { return null; }
        try
        {
            var journal = context.Get<ISessionJournal>(ISessionJournal.NeuronIdOf(owner.Brain));
            var returning = await journal.ReadReturningView();
            foreach (var id in ids)
            {
                var ctx = context.Get<IAgentContext>(id);
                if (string.IsNullOrWhiteSpace(returning.FormattedMarkdown))
                {
                    await ctx.Remove("returning");
                }
                else
                {
                    await ctx.Set(new("returning", returning.FormattedMarkdown, [typeof(ISessionJournal).FullName!]));
                }
            }
            return returning.ActiveProjectBrief;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Session journal context could not be written; the turn runs without it.");
            return null;
        }
    }

    private async Task WriteUiState(string[] keys)
    {
        var ids = ContextsNamed(keys, "ui.state");
        if (ids.Length == 0) { return; }
        try
        {
            var introspector = context.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(owner.Brain));
            foreach (var id in ids)
            {
                await introspector.SyncContext(id.Key);
            }
        }
        catch (Exception error) { logger.LogWarning(error, "UI introspector context could not be written; the turn runs without it."); }
    }

    // A committed tool result keeps a bounded preview: the turn that called the tool saw all of it,
    // later turns only need to know what it was. The result stays valid JSON, paired with its call.
    public static AiMessage Compacted(AiMessage message) => new(message.Role, message.Content.Select(content => content switch
    {
        AiToolResult result when result.ResultJson.Length > MaxStoredToolResultCharacters => new AiToolResult(result.CallId, TruncateJson(result.ResultJson, MaxStoredToolResultCharacters)),
        _ => content,
    }).ToArray());

    public static string CompactToolResult(string resultJson, int maxCharacters = MaxStoredToolResultCharacters)
        => resultJson.Length > maxCharacters ? TruncateJson(resultJson, maxCharacters) : resultJson;

    // Writes {"truncated":true,"characters":N,"preview":"..."} with no reflection (AOT-safe) and without splitting UTF-16 surrogate pairs.
    private static string TruncateJson(string json, int limit)
    {
        var previewLen = Math.Min(limit, json.Length);
        if (previewLen > 0 && char.IsHighSurrogate(json[previewLen - 1]))
        {
            previewLen--;
        }
        var buffer = new ArrayBufferWriter<byte>(previewLen + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("characters", json.Length);
            writer.WriteString("preview", json.AsSpan(0, previewLen));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Writes {"isError":true,"code":"...","message":"..."} with no reflection (AOT-safe).
    internal static string SerializeToolError(string code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(code.Length + message.Length + 48);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("isError", true);
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Recoverable tool refusals are results so the model can continue, but their activity
    // still failed. Keep the original result alongside the diagnostic used by projections.
    internal static (string Code, string Message)? ToolRefusal(string result)
    {
        try
        {
            using var payload = JsonDocument.Parse(result);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("isError", out var error)
                || error.ValueKind != JsonValueKind.True) { return null; }
            var code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var message = root.TryGetProperty("message", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (string.IsNullOrWhiteSpace(code) ? "tool_error" : code,
                string.IsNullOrWhiteSpace(message) ? "The tool reported an error." : message);
        }
        catch (JsonException) { return null; }
    }

    private static AgentToolCall[] Settle(AgentToolCall[] calls, string callId, Func<AgentToolCall, AgentToolCall> update)
        => [.. calls.Select(call => call.CallId == callId ? update(call) : call)];

    // The loop with masking, tail hold-back, cadence and stall stores. Yields the loop's events with masked payloads; tool events
    // update turn.Run.ToolCalls before they are yielded; Completed carries masked, Compacted messages and sets turn.Completed.
    public async IAsyncEnumerable<AgentTurnEvent> Stream(TurnPlan plan, TurnState turn, TurnSink sink, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = 0;
        var stored = 0L;
        var storedVisible = false;

        async Task StoreStep()
        {
            await Refresh(turn, final: false);
            seen = turn.Raw.Length;
            turn.Run = turn.Streamed;
            await sink.Store(turn.Run);
            if (turn.Visible.Length > 0) { storedVisible = true; }
            stored = clock.GetTimestamp();
        }

        var loop = new AgentTurnLoop(services, ReadContext).Run(plan, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var step = loop.MoveNextAsync();
                if (!step.IsCompleted && seen < turn.Raw.Length)
                {
                    // Text held back by the interval is stored once the provider stalls, so what
                    // arrived is never hidden for longer than an interval.
                    var next = step.AsTask();
                    using var stalled = new CancellationTokenSource();
                    var due = ProgressInterval - clock.GetElapsedTime(stored);
                    if (await Task.WhenAny(next, Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, clock, stalled.Token)) != next)
                    {
                        // The step is still running, so this write must not end the turn: the next
                        // store retries it, and that one fails the turn as any write does.
                        try { await StoreStep(); }
                        catch (Exception error) { logger.LogWarning(error, "Run {RunId} could not store its streamed text yet.", plan.RunId); }
                    }
                    await stalled.CancelAsync();
                    step = new(next);
                }
                if (!await step) { break; }
                var current = loop.Current;
                switch (current)
                {
                    case AgentTurnEvent.ModelSelected selected:
                        turn.Run = turn.Run with { Model = selected.Model };
                        await StoreStep();
                        break;
                    case AgentTurnEvent.TextChunk delta:
                        turn.Raw.Append(delta.Content);
                        // The first words are stored at once, later ones at most once per interval.
                        if (!storedVisible || clock.GetElapsedTime(stored) >= ProgressInterval) { await StoreStep(); }
                        break;
                    case AgentTurnEvent.ToolStarted tool:
                        var arguments = await Mask(tool.Arguments);
                        turn.Run = turn.Run with { ToolCalls = [.. turn.Run.ToolCalls, new AgentToolCall(tool.CallId, tool.Name, arguments)] };
                        await StoreStep();
                        current = tool with { Arguments = arguments };
                        break;
                    case AgentTurnEvent.ToolCompleted tool:
                        var result = await Mask(tool.Result);
                        var refusal = ToolRefusal(result);
                        turn.Run = turn.Run with
                        {
                            ToolCalls = Settle(turn.Run.ToolCalls, tool.CallId,
                                call => call with { Result = result, Code = refusal?.Code, Error = refusal?.Message }),
                        };
                        await StoreStep();
                        current = tool with { Result = result };
                        break;
                    case AgentTurnEvent.ToolFailed tool:
                        var message = await Mask(tool.Message);
                        turn.Run = turn.Run with
                        {
                            ToolCalls = Settle(turn.Run.ToolCalls, tool.CallId,
                                call => call with { Code = tool.Code, Error = message }),
                        };
                        await StoreStep();
                        current = tool with { Message = message };
                        break;
                    case AgentTurnEvent.Completed done:
                        await Refresh(turn, final: true);
                        seen = turn.Raw.Length;
                        turn.Run = turn.Run with { Status = AgentRunStatus.Completed, EndedAt = clock.GetUtcNow(), Reply = turn.Visible, Usage = done.Usage };
                        var produced = new List<AiMessage>(done.Messages.Count);
                        foreach (var item in done.Messages) { produced.Add(Compacted(await Mask(item))); }
                        turn.Completed = true;
                        current = done with { Messages = produced };
                        break;
                    case AgentTurnEvent.TurnFailed failed:
                        turn.Failure = failed;
                        break;
                }
                yield return current;
            }
        }
        finally
        {
            await loop.DisposeAsync();
        }
    }
}
