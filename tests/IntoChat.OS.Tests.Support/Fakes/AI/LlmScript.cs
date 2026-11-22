using System.Text.Json;

namespace IntoChat.OS.Tests.Support.Fakes.AI;

/// <summary>A tool call proposed by a scripted model together with the result returned to the model on the next turn.</summary>
public sealed record ToolCallSeen(string Name, string ArgumentsJson, string ResultText);

/// <summary>
/// A deterministic multi-turn script for <see cref="ScriptedChatClient"/>: each <see cref="Turn"/> defines one
/// assistant response (tool calls and/or text) and records every tool result observed on subsequent turns.
/// </summary>
public sealed class LlmScript
{
    private readonly Lock _gate = new();
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<ToolCallSeen> _calls = [];
    private readonly Dictionary<string, (string Name, string ArgumentsJson)> _pending = new(StringComparer.Ordinal);
    private int _nextCallId;

    private LlmScript() { }

    /// <summary>Creates a new empty script.</summary>
    public static LlmScript New() => new();

    /// <summary>Appends one scripted model turn.</summary>
    public LlmScript Turn(Action<ScriptedTurn> turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var built = new ScriptedTurn();
        turn(built);
        lock (_gate)
        {
            _turns.Enqueue(built);
        }
        return this;
    }

    /// <summary>Tool calls whose results have been returned to the model.</summary>
    public IReadOnlyList<ToolCallSeen> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Throws if any scripted turns have not yet been consumed.</summary>
    public void ShouldBeFinished()
    {
        lock (_gate)
        {
            if (_turns.Count > 0)
            {
                throw new InvalidOperationException($"Expected the LLM script to be finished, but {_turns.Count} turn(s) remain.");
            }
        }
    }

    internal ScriptedTurn TakeNext(string lastUserText)
    {
        lock (_gate)
        {
            if (_turns.Count == 0)
            {
                throw new InvalidOperationException($"the script has no turn left; the model was asked: {lastUserText}");
            }
            return _turns.Dequeue();
        }
    }

    internal string RegisterPendingCall(string name, string argumentsJson)
    {
        lock (_gate)
        {
            var callId = $"call_{++_nextCallId}";
            _pending[callId] = (name, argumentsJson);
            return callId;
        }
    }

    internal void RecordToolResult(string callId, string resultText)
    {
        lock (_gate)
        {
            if (_pending.Remove(callId, out var call))
            {
                _calls.Add(new ToolCallSeen(call.Name, call.ArgumentsJson, resultText));
            }
        }
    }
}

/// <summary>Builder for a single scripted assistant turn.</summary>
public sealed class ScriptedTurn
{
    internal sealed record PlannedToolCall(string Name, string ArgumentsJson, Dictionary<string, object?> Arguments);

    internal List<PlannedToolCall> ToolCalls { get; } = [];

    internal List<string> Texts { get; } = [];

    /// <summary>Instructs the model to propose a call to <paramref name="name"/> with <paramref name="arguments"/>.</summary>
    public ScriptedTurn CallsTool(string name, object arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);

        var json = JsonSerializer.Serialize(arguments);
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Tool arguments must serialize to a JSON object.", nameof(arguments));
            }
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }
        }

        ToolCalls.Add(new PlannedToolCall(name, json, map));
        return this;
    }

    /// <summary>Instructs the model to emit assistant text.</summary>
    public ScriptedTurn Says(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Texts.Add(text);
        return this;
    }
}
