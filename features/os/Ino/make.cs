using System.Text.Json;
using IntoChat.OS.Modules.Apps;

// "make": the focused thing does what the sentence says. The sentence becomes one synapse through the fast
// path of the focused app, or of the brain's "scratch" app when nothing focused is an app. Nothing waits for
// the writer: the outcome is the target app's synapse record under this request id. A replayed id is skipped,
// and the fast path itself answers a repeated operation id once.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var key = Ino.Key(brain);
var app = brain.Get<IApp>(key);
var scope = Ino.Scope(brain);
var own = IApp.NameOf(scope, key) ?? "ino";
var ledger = await RequestLedger.LoadAsync(brain, "make");
await foreach (var request in brain.On(app, "make", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try
    {
        var (sentence, focus) = Parse(request.Input);
        var target = await Target(focus);
        await brain.Get<IApp>(IApp.Key(scope, target)).WriteSynapse(sentence, focus, request.RequestId);
        await Ino.Status(brain, InoCopy.Making(target));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, Ino.Failure(InoCopy.MakeFailed, error)); }
    await ledger.Record(request.RequestId);
}

// {"focus": ["<key>", …], "sentence": "…"}; plain text is a sentence with no focus.
static (string Sentence, string[] Focus) Parse(string input)
{
    var trimmed = (input ?? "").Trim();
    if (trimmed.Length == 0 || trimmed[0] != '{')
    {
        if (!trimmed.Any(char.IsLetter)) { throw new ArgumentException("say in a sentence what the focused thing should do."); }
        return (trimmed, []);
    }
    using var document = JsonDocument.Parse(trimmed);
    var root = document.RootElement;
    var sentence = root.TryGetProperty("sentence", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString()!.Trim() : "";
    if (!sentence.Any(char.IsLetter)) { throw new ArgumentException("\"sentence\" says in words what the focused thing should do."); }
    string[] focus = root.TryGetProperty("focus", out var keys) && keys.ValueKind == JsonValueKind.Array
        ? [.. keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Take(16)]
        : [];
    return (sentence, focus);
}

// The first focus key inside one of this brain's installed apps ({brain}/apps/{name} or a neuron under it, such
// as its window) names the target; the assistant never writes into itself. Otherwise the scratch app.
async Task<string> Target(string[] focus)
{
    var prefix = scope + "/apps/";
    foreach (var focused in focus.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
    {
        var rest = focused[prefix.Length..];
        var name = rest.Contains('/') ? rest[..rest.IndexOf('/')] : rest;
        if (name.Length == 0 || name == own || name == "assistant") { continue; }
        if ((await brain.Get<IApp>(IApp.Key(scope, name)).Read()).Manifest.Title.Length > 0) { return name; }
    }
    var scratch = brain.Get<IApp>(IApp.Key(scope, "scratch"));
    if ((await scratch.Read()).Manifest.Title.Length == 0)
    {
        try
        {
            await scratch.Create(new IntoChat.OS.Modules.Apps.AppManifest("Scratch", "Synapses written from single sentences.",
                [new AppOperation("run", "Free-form input for the synapses written here; each reads what it needs.")], [], []),
                "# Scratch\n\nEach synapse here was written from one sentence through the fast path. "
                + "\"run\" carries free-form input to whichever synapse reacts to it.");
        }
        catch (InvalidOperationException)
        {
            // Created meanwhile by another caller (the Engineer shares this app) is fine.
            if ((await scratch.Read()).Manifest.Title.Length == 0) { throw; }
        }
    }
    return "scratch";
}
