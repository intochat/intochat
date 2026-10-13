using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;

// Shared by every synapse: the app's keys, the request record and its neurons, the input rules, the
// classifier call and the list rendering.
public static class Engineer
{
    public const string Scratch = "scratch";
    public const string DefaultModel = "preset:IDeepSeekV41Flash";
    public const int MaxRequest = 4000;
    public const int MaxListed = 200;
    public const int MaxFocus = 16;

    public static class Kinds
    {
        public const string Synapse = "synapse";
        public const string App = "app";
        public const string Module = "module";
    }

    public static class Statuses
    {
        public const string Refused = "Refused";
        public const string Classifying = "Classifying";
        public const string Failed = "Failed";
        public const string Writing = "Writing";
        public const string Written = "Written";
        public const string Rejected = "Rejected";
        public const string Queued = "Queued";
        public const string Building = "Building";
        public const string Built = "Built";
        public const string Blocked = "Blocked";
    }

    // One request of the wish queue, stored as JSON in the text neuron {app}/requests/{id}.
    public sealed record Request
    {
        public string Id { get; init; } = "";
        public string Text { get; init; } = "";
        public string? App { get; init; }
        public string[] Focus { get; init; } = [];
        public string Note { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Build { get; init; } = "";
        public string Reason { get; init; } = "";
        public string Target { get; init; } = "";
        public string Status { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string? Path { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
        public long ClassifyMs { get; init; }
        public DateTimeOffset? WrittenAt { get; init; }
        public long FastPathMs { get; init; }
        public long TotalMs { get; init; }
        public int Attempts { get; init; }
    }

    public sealed record Classification(string Kind, string Build, string? App, string Reason, long ElapsedMs);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Regex AppName = new("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;

    public static string Scope(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static string Name(DigitalBrainConnection brain) => IApp.NameOf(Scope(brain), Key(brain)) ?? "engineer";

    public static IText Index(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/requests");
    public static IText Targets(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/targets");
    public static IText Changes(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/changes");
    // Changed only when an app request is queued, so the builder is not woken by every other change.
    public static IText Builds(DigitalBrainConnection brain) => brain.Get<IText>(Key(brain) + "/builds");
    static IText Neuron(DigitalBrainConnection brain, string id) => brain.Get<IText>(Key(brain) + "/requests/" + id);

    public static async Task<Request?> Read(DigitalBrainConnection brain, string id)
    {
        var markdown = (await Neuron(brain, id).Read()).Markdown;
        if (markdown.Length == 0) { return null; }
        try { return JsonSerializer.Deserialize<Request>(markdown, Json); }
        catch (JsonException) { return null; }
    }

    // Every change to a request is followed by a change of {app}/changes, which the list follows.
    public static async Task Save(DigitalBrainConnection brain, Request request)
    {
        await Neuron(brain, request.Id).Set(JsonSerializer.Serialize(request, Json));
        await Changes(brain).Set(Guid.NewGuid().ToString("N"));
    }

    public static Task Bump(DigitalBrainConnection brain) => Changes(brain).Set(Guid.NewGuid().ToString("N"));

    public static async Task<string[]> Names(IText list)
    {
        var markdown = (await list.Read()).Markdown;
        if (markdown.Length == 0) { return []; }
        try { return JsonSerializer.Deserialize<string[]>(markdown, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    // Only "ask" writes the index and the targets, so their read-modify-write never races.
    public static async Task Add(IText list, string name, int keep = MaxListed)
    {
        var names = await Names(list);
        if (names.Contains(name, StringComparer.Ordinal)) { return; }
        await list.Set(JsonSerializer.Serialize(names.Append(name).TakeLast(keep).ToArray(), Json));
    }

    public static async Task<Request[]> ReadAll(DigitalBrainConnection brain)
    {
        var requests = new List<Request>();
        foreach (var id in await Names(Index(brain)))
        {
            if (await Read(brain, id) is { } request) { requests.Add(request); }
        }
        return [.. requests];
    }

    public static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    // The input rules. A refusal is an ArgumentException whose message is the reason shown to the person.
    public static (string Text, string? App, string[] Focus, string Note) Parse(string input, string scope)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0) { throw new ArgumentException("Ask with a request: plain text, or JSON {\"request\": \"…\", \"app\": \"…\"}."); }
        string text;
        string? app = null;
        string[] focus = [];
        var note = "";
        if (trimmed[0] is '{' or '[')
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                root = document.RootElement.Clone();
            }
            catch (JsonException error) { throw new ArgumentException("The request looks like JSON but does not parse: " + error.Message); }
            if (root.ValueKind != JsonValueKind.Object) { throw new ArgumentException("A JSON request is an object {\"request\": \"…\", \"app\": \"…\"}."); }
            var asked = Text(root, "request") ?? Text(root, "sentence");
            if (string.IsNullOrWhiteSpace(asked)) { throw new ArgumentException("A JSON request says what to change in \"request\", a non-empty text."); }
            text = asked.Trim();
            if (root.TryGetProperty("app", out var named) && named.ValueKind != JsonValueKind.Null)
            {
                if (named.ValueKind != JsonValueKind.String || !AppName.IsMatch(named.GetString()!))
                { throw new ArgumentException("\"app\" names an installed app, such as \"scratch\": lowercase letters, digits and dashes."); }
                app = named.GetString();
            }
            if (root.TryGetProperty("focus", out var keys) && keys.ValueKind != JsonValueKind.Null)
            {
                if (keys.ValueKind != JsonValueKind.Array || keys.EnumerateArray().Any(key => key.ValueKind != JsonValueKind.String))
                { throw new ArgumentException("\"focus\" is a list of neuron keys."); }
                focus = [.. keys.EnumerateArray().Select(key => key.GetString()!.Trim()).Where(key => key.Length > 0)];
            }
            // A wish filed by the dev loop carries who asked and what they saw; it is kept beside the request.
            note = string.Join(" ", new[] { ("persona", Text(root, "persona")), ("observed", Text(root, "observed")) }
                .Where(part => !string.IsNullOrWhiteSpace(part.Item2)).Select(part => $"{part.Item1}: {part.Item2!.Trim()}."));
        }
        else { text = trimmed; }
        if (text.Length > MaxRequest) { throw new ArgumentException($"Say the request in at most {MaxRequest} characters."); }
        if (!text.Any(char.IsLetter)) { throw new ArgumentException("A request says in words what to change."); }
        focus = [.. focus.Concat(KeysIn(text, scope)).Distinct(StringComparer.Ordinal).Take(MaxFocus)];
        return (text, app, focus, note);
    }

    static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Neuron keys the request names ("owner/apps/scratch/echo") are what it is about.
    static IEnumerable<string> KeysIn(string text, string scope)
        => Regex.Matches(text, Regex.Escape(scope) + @"/[A-Za-z0-9._/\-]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')', '/'));

    public static bool IsAppName(string? name) => name is not null && AppName.IsMatch(name);

    public const string Schema = """
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["synapse","app","module"]},
          "build":{"type":"string"},
          "app":{"type":["string","null"]},
          "reason":{"type":"string"}},
         "required":["kind","build","app","reason"],"additionalProperties":false}
        """;

    // One model call. The model writes the JSON object the schema describes; when the model does not
    // declare structured output, the schema rides in the instructions and the reply is read the same way.
    public static async Task<Classification> Classify(DigitalBrainConnection brain, AppSnapshot snapshot, string text, CancellationToken ct)
    {
        var choice = snapshot.Settings.GetValueOrDefault("Model", "");
        var id = choice.Trim().Length > 0 ? choice.Trim() : DefaultModel;
        var model = await brain.Get<IModelCatalog>("default").Select(id)
            ?? throw new InvalidOperationException($"The Model setting names a model this brain cannot use: {id}.");
        // The brain's own model neuron: its inference signals journal into this brain.
        var llm = brain.Get<ILLM>(ModelAddress.Key(Scope(brain)));
        var structured = false;
        try { structured = (await llm.Describe(model)).StructuredOutput == CapabilitySupport.Supported; }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        var instructions = snapshot.Files.GetValueOrDefault("prompts/classify.md", "")
            + (structured ? "" : "\n\nThe JSON schema of your answer:\n" + Schema);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await llm.Generate(new InferenceRequest(
            [new AiMessage("system", [new AiText(instructions)]), new AiMessage("user", [new AiText(await Context(brain, text, ct))])],
            model, new InferenceOptions(MaxOutputTokens: 1200, ResponseSchemaJson: structured ? Schema : null)), ct);
        var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var reply = string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) { throw new InvalidDataException("The classifier did not answer with a JSON object: " + Clip(reply, 200)); }
        using var document = JsonDocument.Parse(reply[start..(end + 1)]);
        var root = document.RootElement;
        var kind = Text(root, "kind")?.Trim().ToLowerInvariant();
        if (kind is not (Kinds.Synapse or Kinds.App or Kinds.Module)) { throw new InvalidDataException($"The classifier answered the kind \"{kind}\", not synapse, app or module."); }
        var app = Text(root, "app")?.Trim().ToLowerInvariant();
        return new(kind, Text(root, "build")?.Trim() ?? "", IsAppName(app) ? app : null, Text(root, "reason")?.Trim() ?? "", elapsed);
    }

    // What the classifier knows of this brain: its apps and the neuron contracts installed.
    static async Task<string> Context(DigitalBrainConnection brain, string text, CancellationToken ct)
    {
        var scope = Scope(brain);
        // App names only: one kernel call, never one call per app.
        var names = new List<string>();
        await foreach (var instance in brain.Kernel.Instances<IApp>(ct))
        {
            if (IApp.NameOf(scope, instance.Key) is { } name) { names.Add(name); }
        }
        var context = new StringBuilder("Installed apps: ").AppendLine(string.Join(", ", names.Order(StringComparer.Ordinal)));
        context.AppendLine().AppendLine("Installed neuron contracts:");
        var budget = 9000;
        foreach (var entry in (await brain.Kernel.Types(ct)).Where(entry => entry.Kind == TypeKind.Neuron).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var line = $"- {entry.Name}: {Clip(entry.Description, 100)}";
            if (line.Length > budget) { break; }
            budget -= line.Length;
            context.AppendLine(line);
        }
        return context.AppendLine().AppendLine("The request:").AppendLine(text).ToString();
    }

    // The scratch app holds the synapses written from sentences that name no app of their own.
    public static async Task<IApp> EnsureScratch(DigitalBrainConnection brain)
    {
        var scratch = brain.Get<IApp>(IApp.Key(Scope(brain), Scratch));
        if ((await scratch.Read()).Manifest.Title.Length > 0) { return scratch; }
        try
        {
            await scratch.Create(new AppManifest("Scratch", "Synapses the Engineer wrote from single sentences.",
                [new AppOperation("run", "Free-form input for the synapses written here; each reads what it needs.")], [], []),
                "# Scratch\n\nThe Engineer's scratch app: each synapse here was written from one sentence through the fast path. "
                + "\"run\" carries free-form input to whichever synapse reacts to it.");
        }
        catch (InvalidOperationException)
        {
            // Created meanwhile by another caller is fine; any other refusal is the request's failure.
            if ((await scratch.Read()).Manifest.Title.Length == 0) { throw; }
        }
        return scratch;
    }

    static string Seconds(long milliseconds) => (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    // The list is rebuilt from the requests every time, never patched. Newest first.
    public static async Task RenderAsync(DigitalBrainConnection brain)
    {
        var requests = (await ReadAll(brain)).OrderByDescending(request => request.ReceivedAt).ToArray();
        var lines = new StringBuilder("# Requests\n\n");
        if (requests.Length == 0) { lines.Append("_No requests yet. Ask the Engineer in the dock: say what you want your brain to do._"); }
        foreach (var request in requests)
        {
            var kind = request.Kind.Length == 0 ? "" : $" · {request.Kind}" + (request.Target.Length == 0 ? "" : $" → {request.Target}");
            lines.Append($"- **{request.Status}**{kind} — {(request.Text.Length == 0 ? "_(empty)_" : Clip(request.Text, 160))}");
            if (request.Status == Statuses.Written)
            { lines.Append($" — `{request.Path}` live in {Seconds(request.TotalMs)} (fast path {Seconds(request.FastPathMs)}, {request.Attempts} model call{(request.Attempts == 1 ? "" : "s")})"); }
            else if (request.Outcome.Length > 0) { lines.Append(" — ").Append(Clip(request.Outcome, 300)); }
            lines.Append($" · {request.ReceivedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC\n");
        }
        var counts = requests.GroupBy(request => request.Status).OrderByDescending(group => group.Count())
            .Select(group => $"{group.Count()} {group.Key.ToLowerInvariant()}");
        var summary = requests.Length == 0 ? "No requests yet." : $"{requests.Length} request{(requests.Length == 1 ? "" : "s")}: {string.Join(", ", counts)}.";
        await brain.Get<IText>(Key(brain) + "/status").Set(summary);
        await brain.Get<IText>(Key(brain) + "/list").Set(lines.ToString());
    }

    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        var main = brain.Get<ILayout>(appKey + "/main");
        await main.Set(new("list", [new(UIVocabulary.TextType, appKey + "/status"), new(UIVocabulary.TextType, appKey + "/list")]), (await main.Read()).Revision);
        await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main")));
    }

}
