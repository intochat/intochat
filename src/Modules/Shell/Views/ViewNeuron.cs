using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Errors;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Views;

// Stores an app's View as its wire JSON, verbatim; resolves an activation by key and hands the typed action to the edge.
[Neuron(UIVocabulary.ViewType)]
internal sealed class ViewNeuron
    : UiNeuron<ViewState>, IView
{
    public async Task Publish(long generation, View view)
    {
        RequireWriter();
        var json = ViewValidation.Serialize(view);
        if (generation < Snapshot.Source || json == Snapshot.Json) { return; }
        var next = checked(Snapshot.Generation + 1);
        await Save(new() { Json = json, Generation = next, Source = generation, Changes = Snapshot.Changes + 1 }, new ViewChanged(FullKey, next));
        // The view carries no field values; it tells its own app's field neurons their kind, so a secret is never stored. Values
        // stay with the field: nothing is seeded, the app pre-fills with IField.Set.
        var app = AppPrefix(Id.Key);
        foreach (var (name, kind) in ViewValidation.Fields(ViewValidation.Parse(json)))
        {
            if (app is not null && name.StartsWith(app, StringComparison.Ordinal) && name.Length > app.Length && !name.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.')))
            { await Get<Fields.IField>(new DigitalBrain.NeuronId(Id.Brain, UIVocabulary.FieldType, name)).Configure(kind); }
        }
    }

    // "apps/{app}/" of a view keyed under an app, else null.
    private static string? AppPrefix(string key)
    {
        var parts = key.Split('/');
        return parts.Length > 2 && parts[0] == "apps" ? $"apps/{parts[1]}/" : null;
    }

    [DigitalBrain.Contracts.ReadOnlyCall]
    public Task<PublishedView?> Read() => Task.FromResult(Snapshot.Json is { } json
        ? new PublishedView(FullKey, Snapshot.Generation, ViewValidation.Parse(json))
        : null);

    [DigitalBrain.Contracts.ReadOnlyCall]
    public Task<ViewActivation> Activate(long generation, string key, string? fieldValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Snapshot.Json is not { } json || Snapshot.Generation != generation
            || ViewValidation.Find(ViewValidation.Parse(json), key) is not { } node
            || node.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
        { throw new StaleActionException("This action is no longer available. Read the view again."); }
        return Task.FromResult(ViewValidation.ActionOf(node) with { Value = fieldValue });
    }

    private void RequireWriter()
    {
        // A call with no stamped caller is refused, never taken for the platform.
        var caller = RuntimeCallerContext.TryGet(out var stamped) ? stamped : throw new UnauthorizedAccessException("No caller was stamped on this call; a view is published only by a stamped one.");
        if (!Writes(caller, FullKey)) { throw new UnauthorizedAccessException("Only the owning brain and app may publish this view."); }
    }

    // The brain's person or platform, or the app that owns the key ({app}/…); never another brain.
    internal static bool Writes(DigitalBrain.Capabilities.CapabilityCaller caller, string name)
        => BrainKey.BelongsTo(name, caller.BrainId) && (caller.Kind != CallerKind.App ||
            !string.IsNullOrEmpty(caller.AppId) && name.StartsWith(caller.AppId + "/", StringComparison.Ordinal));
}

[GenerateSerializer, Alias("ui.view-state.v2")]
[ExportableState]
internal sealed class ViewState
{
    // The View's wire JSON as published.
    [Id(0)] public string? Json { get; set; }
    // The neuron's own counter, increased by every stored change; activations name it.
    [Id(1)] public long Generation { get; set; }
    // The publisher's program generation of the stored view: an older one is ignored.
    [Id(2)] public long Source { get; set; }
    // Publications so far: the retained ViewChanged's sequence.
    [Id(3)] public long Changes { get; set; }
}

internal static class ViewValidation
{
    private static readonly JsonDocumentOptions Document = new() { MaxDepth = 256 };

    public static string Serialize(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var json = JsonSerializer.Serialize(view, RuntimeProtocol.OptionsFor(typeof(View)));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576) { throw new ArgumentException("A view is limited to 1 MiB."); }
        using var document = JsonDocument.Parse(json, Document);
        var count = 0;
        Walk(document.RootElement, 0);
        return json;

        void Walk(JsonElement element, int depth)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
                {
                    if (++depth > 32 || ++count > 512) { throw new ArgumentException("A view is a tree of at most 512 nodes and depth 32."); }
                }
                foreach (var property in element.EnumerateObject().Where(p => p.Name is not ("emit" or "args"))) { Walk(property.Value, depth); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { Walk(item, depth); }
            }
        }
    }

    // Every Field node's name (its field neuron's key) and kind, depth first.
    public static IEnumerable<(string Name, FieldKind Kind)> Fields(JsonElement root)
    {
        var pending = new Stack<JsonElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (Text(element, "name") is { Length: > 0 } name && element.TryGetProperty("fieldKind", out var kind) && KindOf(kind) is { } parsed)
                { yield return (name, parsed); }
                foreach (var property in element.EnumerateObject().Where(p => p.Name is not ("emit" or "args"))) { pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { pending.Push(item); }
            }
        }
    }

    private static FieldKind? KindOf(JsonElement kind)
        => kind.ValueKind == JsonValueKind.String && Enum.TryParse<FieldKind>(kind.GetString(), ignoreCase: true, out var parsed) ? parsed
            : kind.ValueKind == JsonValueKind.Number && kind.TryGetInt32(out var number) && Enum.IsDefined((FieldKind)number) ? (FieldKind)number
            : null;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, Document);
        return document.RootElement.Clone();
    }

    // The first node, depth first, whose "key" is the key: an object with a "kind" or an action member; emit and args are data.
    public static JsonElement? Find(JsonElement root, string key)
    {
        var pending = new Stack<JsonElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key
                    && (Has(element, "kind", JsonValueKind.String) || Has(element, "emit", JsonValueKind.Object) || Has(element, "intent", JsonValueKind.Object)
                        || Has(element, "resolve", JsonValueKind.Object) || Has(element, "onSubmit", JsonValueKind.Object)))
                { return element; }
                foreach (var property in element.EnumerateObject().Reverse().Where(p => p.Name is not ("emit" or "args"))) { pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray().Reverse()) { pending.Push(item); }
            }
        }
        return null;
    }

    public static ViewActivation ActionOf(JsonElement node)
    {
        if (node.TryGetProperty("emit", out var emit) && emit.ValueKind == JsonValueKind.Object
            || node.TryGetProperty("onSubmit", out emit) && emit.ValueKind == JsonValueKind.Object)
        {
            if (Text(emit, "owner") is { } owner && Text(emit, "signal") is { } signal)
            {
                JsonElement? args = emit.TryGetProperty("args", out var a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? a.Clone() : null;
                return new(ViewActivationKind.Emit, Emit: new Emit(owner, signal, args));
            }
            return ViewActivation.None;
        }
        var intent = node.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.Object ? i
            : Text(node, "kind") == "intent" ? node : (JsonElement?)null;
        if (intent is { } found && Text(found, "intentKind") is { } kind && Enum.TryParse<IntentKind>(kind, ignoreCase: true, out var parsed))
        { return new(ViewActivationKind.Intent, Intent: new Intent(parsed, Text(found, "arg"))); }
        return ViewActivation.None;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Has(JsonElement element, string name, JsonValueKind kind) => element.TryGetProperty(name, out var value) && value.ValueKind == kind;
}
