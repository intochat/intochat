using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Flutter.Views;

// Stores an app's View as its wire JSON, verbatim. It is the screen of a sandboxed app: POST /act and screen_act reach it through
// IScreen.Dispatch, and a command to "." becomes the owning app's request.
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

    public Task<PublishedView?> Open() => Read();

    public Task<PublishedView?> Read(string? alias) => Read();

    // A view neuron has no children: no key path reaches one.
    public Task<PublishedView?> ReadChild(string path)
        => Task.FromException<PublishedView?>(new ActivationRefusedException(ActivationRefusedException.UnknownChild));

    // A command to "." is the owning app's request ({brain}/apps/{name}), only for a signal the current view sends to ".", and with
    // the acting person stamped under "by" when the args are an object (a positional array has no member to carry it). A command
    // to any other target has nowhere to go: the app has no children.
    public async Task Dispatch(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireSameBrain();
        if (!ViewValidation.ToSelf(command)) { throw new ActivationRefusedException(ActivationRefusedException.NoChildren); }
        if (Snapshot.Json is not { } json || !ViewValidation.SendsToSelf(ViewValidation.Parse(json), command.Signal))
        {
            throw new ActivationRefusedException(ActivationRefusedException.NoEmit, $"The view of {FullKey} sends no '{command.Signal}'.");
        }
        await Emit(command.Signal, command.Args, Context.Caller?.For?.Principal);
    }

    public Task Request(string signal, JsonElement? args = null) => Dispatch(new Command(".", signal, args));

    private Task Emit(string signal, JsonElement? args, string? by)
    {
        if (OwningApp() is not { } app) { throw new UnauthorizedAccessException($"{FullKey} is not an app's view."); }
        return Get<IApp>(app).Emit(signal, ViewArgs.Stamped(args, value: null, by).GetRawText());
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

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Has(JsonElement element, string name, JsonValueKind kind) => element.TryGetProperty(name, out var value) && value.ValueKind == kind;
}
