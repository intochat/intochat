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
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Flutter.Views;

// Stores an app's View as its wire JSON, verbatim; resolves an activation by key and hands the typed action to the edge. It is the
// screen of a sandboxed app: POST /act reaches it through IScreen.Dispatch, and a command to "." becomes the owning app's request.
[Neuron(UIVocabulary.ViewType)]
internal sealed partial class ViewNeuron
    : UiNeuron<ViewState>, IView, IScreen
{
    public async Task Publish(long generation, View view)
    {
        RequireWriter();
        var json = ViewValidation.Serialize(view);
        if (generation < Snapshot.Source || json == Snapshot.Json) { return; }
        var next = checked(Snapshot.Generation + 1);
        await Save(new() { Json = json, Generation = next, Source = generation, Changes = Snapshot.Changes + 1 }, new FlutterViewChanged(FullKey, next));
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

    public Task<PublishedView?> Open() => Read();

    public Task<PublishedView?> Read(string? alias) => Read();

    // A view neuron has no children: no key path reaches one.
    public Task<PublishedView?> ReadChild(string path)
        => Task.FromException<PublishedView?>(new ActivationRefusedException(ActivationRefusedException.UnknownChild));

    // A command to "." is the owning app's request ({brain}/apps/{name}), only for a signal the current view sends to ".", and with
    // the acting person stamped under "by" when the args are an object (a positional array has no member to carry it). A command
    // to any other target has nowhere to go: the app has no child slots.
    public async Task Dispatch(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireSameBrain();
        if (!ViewValidation.ToSelf(command)) { throw new ActivationRefusedException(ActivationRefusedException.NoSlots); }
        if (Snapshot.Json is not { } json || !ViewValidation.SendsToSelf(ViewValidation.Parse(json), command.Signal))
        {
            throw new ActivationRefusedException(ActivationRefusedException.NoEmit, $"The view of {FullKey} sends no '{command.Signal}'.");
        }
        await Emit(command.Signal, command.Args, fieldValue: null, Context.Caller?.For?.Principal);
    }

    public Task Request(string signal, JsonElement? args = null) => Dispatch(new Command(".", signal, args));

    public Task Act(ScreenAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Activate(action.Alias ?? FullKey, action.Generation, action.NodeKey, action.FieldValue, Context.Caller?.For?.Principal);
    }

    // Key activation, as IScreen still offers it to agents: the node's command to "." goes to the owning app.
    public async Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null)
    {
        RequireSameBrain();
        var action = await Activate(generation, nodeKey, fieldValue);
        if (action is not { Kind: ViewActivationKind.Command, Command: { } command }) { throw new ActivationRefusedException(ActivationRefusedException.NoEmit); }
        if (!ViewValidation.ToSelf(command)) { throw new ActivationRefusedException(ActivationRefusedException.NoSlots); }
        await Emit(command.Signal, command.Args, fieldValue, by);
    }

    private Task Emit(string signal, JsonElement? args, string? fieldValue, string? by)
    {
        if (OwningApp() is not { } app) { throw new UnauthorizedAccessException($"{FullKey} is not an app's view."); }
        return Get<IApp>(app).Emit(signal, ViewArgs.Stamped(args, fieldValue, by).GetRawText());
    }

    // The app whose view this is: apps/{name}/view in this brain.
    private NeuronId? OwningApp()
        => Id.Key.Split('/') is ["apps", var name, ..] && IntoChat.OS.Host.Primitives.AppRef.TryOf(Id.Brain, name, out var app)
            ? DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IApp), app.Key)
            : null;

    private void RequireWriter()
    {
        // A call with no stamped caller is refused, never taken for the platform.
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("No caller was stamped on this call; a view is published only by a stamped one.");
        if (!Writes(caller, FullKey)) { throw new UnauthorizedAccessException("Only the owning brain and app may publish this view."); }
    }

    // The brain's person or platform, or the app that owns the key ({app}/…); never another brain.
    internal static bool Writes(DigitalBrain.Access.Caller caller, string name)
        => BrainKey.BelongsTo(name, caller.Brain.Value) && (!DigitalBrain.Access.CallerRules.IsApp(caller, out var app) ||
            !string.IsNullOrEmpty(app) && name.StartsWith(app + "/", StringComparison.Ordinal));
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

    // The command target that names the view's own neuron (DigitalBrain.UI.Command).
    private const string SelfTarget = ".";

    // Whether the command goes to the view's own neuron rather than a child address.
    public static bool ToSelf(Command command) => string.Equals(command.Target, SelfTarget, StringComparison.Ordinal);

    public static string Serialize(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var json = System.Text.Encoding.UTF8.GetString(RuntimeProtocol.SerializeToUtf8Bytes(view, typeof(View)));
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
                foreach (var property in element.EnumerateObject().Where(p => p.Name is not ("command" or "close" or "onSubmit" or "args"))) { Walk(property.Value, depth); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { Walk(item, depth); }
            }
        }
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json, Document);
        // A view stored before every action was a command reads its legacy emits as commands.
        return LegacyViewJson.Normalize(document.RootElement.Clone());
    }

    // The first node, depth first, whose "key" is the key: an object with a "kind" or an action member; commands and args are data.
    public static JsonElement? Find(JsonElement root, string key)
    {
        var pending = new Stack<JsonElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key
                    && (Has(element, "kind", JsonValueKind.String) || Has(element, "command", JsonValueKind.Object) || Has(element, "close", JsonValueKind.Object) || Has(element, "intent", JsonValueKind.Object)
                        || Has(element, "resolve", JsonValueKind.Object) || Has(element, "onSubmit", JsonValueKind.Object)))
                { return element; }
                foreach (var property in element.EnumerateObject().Reverse().Where(p => p.Name is not ("command" or "close" or "onSubmit" or "args"))) { pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray().Reverse()) { pending.Push(item); }
            }
        }
        return null;
    }

    // Whether any node of the view sends signal to "." (a button's command, an info bar's close, a field's onSubmit).
    public static bool SendsToSelf(JsonElement root, string signal)
    {
        var pending = new Stack<JsonElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "command" or "close" or "onSubmit" && property.Value.ValueKind == JsonValueKind.Object
                        && Text(property.Value, "target") == SelfTarget && Text(property.Value, "signal") == signal)
                    { return true; }
                    if (property.Name is not "args") { pending.Push(property.Value); }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { pending.Push(item); }
            }
        }
        return false;
    }

    // A node's action: its command (to "." for the view's own app, or to a child address, which a slotless view cannot reach) or its
    // client-local intent. The view was normalized on parse (LegacyViewJson), so a stored emit reads as its command.
    public static ViewActivation ActionOf(JsonElement node)
    {
        // An info bar's command is its "close", a field's is its "onSubmit"; no other kind reads them. A kindless part (a chat composer,
        // a model picker) sends its "command", or else its "onSubmit".
        var member = Text(node, "kind") switch
        {
            "infoBar" => "close",
            "field" => "onSubmit",
            null => Has(node, "command", JsonValueKind.Object) ? "command" : "onSubmit",
            _ => "command",
        };
        if (node.TryGetProperty(member, out var command) && command.ValueKind == JsonValueKind.Object)
        {
            if (Text(command, "target") is not { Length: > 0 } target || Text(command, "signal") is not { Length: > 0 } sent) { return ViewActivation.None; }
            JsonElement? sentArgs = command.TryGetProperty("args", out var c) && c.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? c.Clone() : null;
            return new(ViewActivationKind.Command, Command: new Command(target, sent, sentArgs));
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
