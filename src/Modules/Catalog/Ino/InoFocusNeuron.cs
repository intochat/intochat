using System.Text.Json;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("ino.focus"), DigitalBrain.Neurons.StatelessNeuron]
internal sealed class InoFocusNeuron : DigitalBrain.Neurons.Neuron, IFocus
{
    private IAgentContext Focus => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", IFocus.ContextKey));

    public async Task<InoToolResult> Point(string target, FocusTone tone, string reason, FocusAction? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(reason);
        if (action is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(action.Kind); }
        if (tone == FocusTone.Critical && ContextTrust.Of(Context.Caller) == ContextTrust.Model)
        {
            return new InoToolResult("Critical is reserved for the platform", Ok: false);
        }
        var text = JsonSerializer.Serialize(new FocusJson(target.Trim(), tone.ToString(), reason,
            action is null ? null : new FocusActionJson(action.Kind, action.Argument ?? "")), PresenceText.Json);
        try { await Focus.Set(new ContextEntry(IFocus.EntryName, text, [], [ContextTags.UiFocus])); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoFocusNeuron>().LogSwallowed(error, "pointing Ino focus");
            return new InoToolResult($"Could not point at {target}: {error.Message}", Ok: false);
        }
        return new InoToolResult($"Pointing at {target}.");
    }

    public async Task<InoToolResult> Clear()
    {
        try { await Focus.Remove(IFocus.EntryName); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoFocusNeuron>().LogSwallowed(error, "clearing Ino focus");
            return new InoToolResult($"Could not clear the focus: {error.Message}", Ok: false);
        }
        return new InoToolResult("Cleared the focus.");
    }

    public Task<InoToolResult> Show(string title, string node)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Task.FromResult(new InoToolResult("Title is required.", Ok: false));
        }

        if (!InoNodeCard.TryRead(node, out _, out var refusal))
        {
            return Task.FromResult(new InoToolResult(refusal, Ok: false));
        }

        var id = $"node-{Guid.NewGuid():N}";
        var card = new InoCard(id, "node", title.Trim(), string.Empty, IFocus.DefaultKey, "done") { Node = node };
        return Task.FromResult(new InoToolResult("Shown.", card));
    }
}

internal sealed record FocusActionJson(string Kind, string Argument);

internal sealed record FocusJson(string Target, string Tone, string Reason, FocusActionJson? Action);

internal static class PresenceText
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // The focus a ui.focus entry names, or null when its text is not a focus. A model may not raise a Critical focus.
    public static Focus? FocusOf(ContextEntry entry)
    {
        FocusJson? parsed;
        try { parsed = JsonSerializer.Deserialize<FocusJson>(entry.Text, Json); }
        catch (JsonException) { return null; }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Target) || parsed.Reason is null
            || !Enum.TryParse<FocusTone>(parsed.Tone, ignoreCase: true, out var tone) || !Enum.IsDefined(tone)) { return null; }
        if (tone == FocusTone.Critical && entry.Provenance?.Trust is not (ContextTrust.App or ContextTrust.Platform)) { return null; }
        var action = parsed.Action is { Kind: { Length: > 0 } kind } a ? new FocusAction(kind, a.Argument ?? "") : null;
        return new Focus(parsed.Target, tone, parsed.Reason, action);
    }
}
