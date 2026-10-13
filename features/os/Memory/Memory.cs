using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI.Embeddings;
using DigitalBrain.AI.Media;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;

// Shared: the rows Memory indexes, the generation recall reads, how text becomes terms and meaning
// vectors, how a question ranks the rows, and the markdown the surface shows.
static class Memory
{
    public const int ShownSynapses = 5;
    public const int ShownOthers = 3;
    public const int EmbedBatch = 128;
    public const int RecentRuns = 31;
    const int MaxTerms = 200;
    const int ExcerptLength = 200;
    const int CodeHead = 1500;
    const int SpecHead = 600;
    const int SpecMeaning = 2500;
    const double WordsWeight = 0.5;
    // Calibrated on the deployment's embedding model: rows a question is about stand 0.30 to 0.44 above the
    // brain's median row; for a question nothing is about, short scratch rows stand up to 0.14 above it.
    const double NearLift = 0.18;

    // Meaning is the text a row is embedded from; it is built at reindex and never stored in the table.
    public sealed record Row(string Id, string App, string Path, string Kind, string Title, string Version,
        Dictionary<string, int> Terms, string Excerpt, long Runs, DateTimeOffset? SavedAt, long Generation = 0, string Meaning = "");

    // The complete generation recall reads: reindex writes every row of it first and switches here last.
    // Fingerprint is a hash of everything the rows store and embed, so an unchanged brain is not rebuilt.
    public sealed record Generation(long Number, int Rows, int Vectors, string Note, DateTimeOffset? BuiltAt, string Fingerprint);

    public sealed record Index(Generation Generation, List<Row> Rows);

    public sealed record Ranked(Row Row, double Score, double Words, double? Meaning);

    public sealed record Sample(string Mark, long Microseconds);

    // Table definitions are frozen once a table exists ("migrations are not supported"): a new shape takes a new key.
    public static readonly TableDefinition RowsDefinition = new(
    [
        new("generation", "number"), new("id", "text"), new("app", "text"), new("path", "text"), new("kind", "text"),
        new("title", "text"), new("version", "text"), new("excerpt", "text"), new("terms", "jsonb"), new("runs", "number"),
        new("saved_at", "timestamptz"),
    ], ["generation", "id"]);

    public static readonly TableDefinition GenerationsDefinition = new(
    [
        new("name", "text"), new("generation", "number"), new("rows", "number"), new("vectors", "number"),
        new("note", "text"), new("built_at", "timestamptz"), new("fingerprint", "text"),
    ], ["name"]);

    // The requests an operation already answered, for operations whose result is not addressed by request id.
    public static readonly TableDefinition RequestsDefinition = new(
    [
        new("request", "text"), new("operation", "text"), new("answered_at", "timestamptz"),
    ], ["request"]);

    public static readonly TableDefinition RunsDefinition = new(
    [
        new("behavior", "text"), new("hash", "text"), new("recent", "jsonb"), new("p50", "number"), new("updated_at", "timestamptz"),
    ], ["behavior"]);

    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "do", "does", "for", "from", "has", "have", "if", "in", "into",
        "is", "it", "its", "of", "on", "or", "so", "than", "that", "the", "their", "then", "there", "these", "this",
        "to", "was", "were", "what", "when", "which", "while", "who", "will", "with", "you", "your",
        "args", "async", "await", "class", "const", "cs", "digitalbrain", "else", "false", "foreach", "internal", "int",
        "new", "null", "private", "public", "readonly", "record", "return", "sealed", "static", "string", "system",
        "task", "true", "using", "var", "void",
    };

    public static string Scope(DigitalBrainConnection brain)
    {
        var appKey = brain.Setting("App")!;
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static string RowsKey(string appKey) => appKey + "/rows";
    public static string GenerationsKey(string appKey) => appKey + "/current";
    public static string RequestsKey(string appKey) => appKey + "/requests";
    public static string RunsKey(string appKey) => appKey + "/runs";
    public static string VectorsKey(string scope) => IVectorIndex.Key(scope, "apps");
    public static string EmbeddingKey(string scope) => scope + "/embedding";

    // This brain's installed apps, by name.
    public static async Task<(string Name, AppSnapshot State)[]> Apps(DigitalBrainConnection brain)
    {
        var scope = Scope(brain);
        var apps = new List<(string Name, AppSnapshot State)>();
        await foreach (var instance in brain.Kernel.Instances<IApp>(brain.Stopping))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name) { continue; }
            var state = await brain.Get<IApp>(instance.Key).Read();
            if (state.Manifest.Title.Length > 0) { apps.Add((name, state)); }
        }
        return [.. apps.OrderBy(app => app.Name, StringComparer.Ordinal)];
    }

    // What the brain runtime reports for each behavior; null when the runtime could not be asked.
    public static async Task<BehaviorView[]?> Behaviors(DigitalBrainConnection brain)
    {
        try { return [.. (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Scope(brain))).Inspect()).Behaviors]; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return null; }
    }

    // Every row of the index as the installed apps are now, Memory's own program left out.
    public static async Task<Row[]> Collect(DigitalBrainConnection brain)
    {
        var appKey = brain.Setting("App")!;
        var scope = Scope(brain);
        var behaviors = await Behaviors(brain) ?? [];
        return [.. (await Apps(brain)).Where(app => IApp.Key(scope, app.Name) != appKey)
            .SelectMany(app => RowsOf(scope, app.Name, app.State, behaviors))];
    }

    // One row for the app (what it is for) and one per program file; a file's row also carries its app's
    // title and description, so a synapse is found by what its app does. Runs belong to the synapse that
    // ran: an app row holds none, so a run is counted once.
    static Row[] RowsOf(string scope, string name, AppSnapshot state, BehaviorView[] behaviors)
    {
        var key = IApp.Key(scope, name);
        var about = name.Replace('-', ' ') + "\n" + state.Manifest.Title + "\n" + state.Manifest.Description;
        DateTimeOffset? saved = state.Versions.Length == 0 ? null : state.Versions.Max(version => version.SavedAt);
        var files = state.Files.Where(file => AppFiles.IsProgram(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file =>
            {
                var kind = Shape.IsSynapse(file.Value) ? "synapse" : "shared";
                var summary = Summary(file.Value);
                var meaning = $"{name}/{file.Key} · {kind} of {state.Manifest.Title}: {state.Manifest.Description}\n{summary}\n"
                    + Head(Code(file.Value), CodeHead) + "\n" + Head(state.Spec, SpecHead);
                return new Row(name + "/" + file.Key, name, file.Key, kind, state.Manifest.Title, state.Version,
                    Weigh((file.Value, 1), (file.Key[..^".cs".Length], 3), (about, 2)), summary,
                    kind == "synapse" ? Find(behaviors, BehaviorIds.For(key, file.Key))?.Runs ?? 0 : 0, saved, Meaning: meaning);
            })
            .ToArray();
        var operations = string.Join("\n", state.Manifest.Operations.Select(operation => operation.Name + " " + operation.Description));
        var app = new Row(name, name, "", "spec", state.Manifest.Title, state.Version,
            Weigh((about, 3), (state.Spec, 1), (operations, 1)), Excerpt(state.Manifest.Description), 0, saved,
            Meaning: $"{name} · app {state.Manifest.Title}: {state.Manifest.Description}\nOperations:\n{operations}\n{Head(state.Spec, SpecMeaning)}");
        return [app, .. files];
    }

    public static BehaviorView? Find(BehaviorView[]? behaviors, string id) => behaviors?.FirstOrDefault(behavior => behavior.Id == id);

    // The source without its using directives and blank lines: what the file does, not what it imports.
    static string Code(string source)
        => string.Join("\n", source.Split('\n').Select(line => line.TrimEnd())
            .Where(line => line.Length > 0 && !line.StartsWith("using ", StringComparison.Ordinal)));

    static string Head(string text, int length) => text.Length <= length ? text : text[..length];

    // Embeds texts in batches the model accepts; each vector answers the text at its position.
    public static async Task<float[][]> Embed(DigitalBrainConnection brain, IReadOnlyList<string> texts)
    {
        var model = brain.Get<IEmbeddingModel>(EmbeddingKey(Scope(brain)));
        var vectors = new List<float[]>(texts.Count);
        for (var start = 0; start < texts.Count; start += EmbedBatch)
        {
            var batch = texts.Skip(start).Take(EmbedBatch).Select(text => text.Trim()).ToArray();
            var result = await model.Embed(new EmbeddingRequest(batch), brain.Stopping);
            if (result.Vectors.Length != batch.Length)
            { throw new InvalidOperationException($"The embedding model answered {result.Vectors.Length} vectors for {batch.Length} texts."); }
            vectors.AddRange(result.Vectors);
        }
        return [.. vectors];
    }

    // What the rows store and embed, runs left out: runs change with every run and recall reads them live.
    public static string Fingerprint(IEnumerable<Row> rows)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            rows.OrderBy(row => row.Id, StringComparer.Ordinal).Select(row => row with { Runs = 0, Generation = 0 })))));

    // The runs the brain runtime counts now, for each synapse row; rows keep what was stored when it did not answer.
    public static List<Row> WithRuns(string scope, IEnumerable<Row> rows, BehaviorView[]? behaviors)
        => [.. rows.Select(row => row.Kind == "synapse" && Find(behaviors, BehaviorIds.For(IApp.Key(scope, row.App), row.Path)) is { } behavior
            ? row with { Runs = behavior.Runs } : row)];

    static TableValue[] RequestKey(Guid request) => [new("request", JsonSerializer.Serialize(request.ToString("N")))];

    public static async Task<bool> Answered(ITable requests, AppRequested request)
        => await requests.Read(RequestKey(request.RequestId)) is not null;

    public static Task<bool> Answer(ITable requests, AppRequested request)
        => requests.Upsert(RequestKey(request.RequestId),
            [new("operation", JsonSerializer.Serialize(request.Operation)), new("answered_at", Time(DateTimeOffset.UtcNow))]);

    public static TableValue[] Key(Row row)
        => [new("generation", row.Generation.ToString(CultureInfo.InvariantCulture)), new("id", JsonSerializer.Serialize(row.Id))];

    public static TableValue[] Values(Row row) =>
    [
        new("app", JsonSerializer.Serialize(row.App)), new("path", JsonSerializer.Serialize(row.Path)),
        new("kind", JsonSerializer.Serialize(row.Kind)), new("title", JsonSerializer.Serialize(row.Title)),
        new("version", JsonSerializer.Serialize(row.Version)), new("excerpt", JsonSerializer.Serialize(row.Excerpt)),
        new("terms", JsonSerializer.Serialize(row.Terms)), new("runs", row.Runs.ToString(CultureInfo.InvariantCulture)),
        new("saved_at", Time(row.SavedAt)),
    ];

    public static readonly TableValue[] CurrentKey = [new("name", "\"current\"")];

    public static TableValue[] Values(Generation generation) =>
    [
        new("generation", generation.Number.ToString(CultureInfo.InvariantCulture)),
        new("rows", generation.Rows.ToString(CultureInfo.InvariantCulture)),
        new("vectors", generation.Vectors.ToString(CultureInfo.InvariantCulture)),
        new("note", JsonSerializer.Serialize(generation.Note)), new("built_at", Time(generation.BuiltAt)),
        new("fingerprint", JsonSerializer.Serialize(generation.Fingerprint)),
    ];

    static string Time(DateTimeOffset? time)
        => time is { } at ? JsonSerializer.Serialize(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : "null";

    public static async Task<Generation?> ReadGeneration(ITable generations)
    {
        if (await generations.Read(CurrentKey) is not { } cells) { return null; }
        var cell = new Cells(cells);
        return new((long)cell.Number("generation"), (int)cell.Number("rows"), (int)cell.Number("vectors"), cell.Text("note"), cell.Time("built_at"),
            cell.Text("fingerprint"));
    }

    // The current generation, whole: rows are read after the switch they were written before, and a count
    // that disagrees with the generation (a rebuild switched and pruned meanwhile) is a conflict the caller rebuilds from.
    public static async Task<Index?> ReadIndex(ITable generations, ITable rows, CancellationToken cancellationToken)
    {
        if (await ReadGeneration(generations) is not { } generation) { return null; }
        var current = (await ReadAll(rows, cancellationToken)).Where(row => row.Generation == generation.Number).ToList();
        if (current.Count != generation.Rows) { throw new RevisionConflictException("The memory index switched generation while it was read.", generation.Number); }
        return new(generation, current);
    }

    public static async Task<List<Row>> ReadAll(ITable table, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        foreach (var cells in await Pages(table, cancellationToken)) { rows.Add(Parse(cells)); }
        return rows;
    }

    static async Task<List<TableValue[]>> Pages(ITable table, CancellationToken cancellationToken)
    {
        var all = new List<TableValue[]>();
        for (var offset = 0; ; offset += PostgresQuery.MaxRowsLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await table.Page(offset, PostgresQuery.MaxRowsLimit);
            all.AddRange(page);
            if (page.Length < PostgresQuery.MaxRowsLimit) { return all; }
        }
    }

    static Row Parse(TableValue[] cells)
    {
        var cell = new Cells(cells);
        return new(cell.Text("id"), cell.Text("app"), cell.Text("path"), cell.Text("kind"), cell.Text("title"), cell.Text("version"),
            new(cell.Json<Dictionary<string, int>>("terms") ?? [], StringComparer.Ordinal), cell.Text("excerpt"),
            (long)cell.Number("runs"), cell.Time("saved_at"), (long)cell.Number("generation"));
    }

    // Cells arrive as JSON literals, as the table stores them.
    sealed class Cells(TableValue[] cells)
    {
        readonly Dictionary<string, string> json = cells.ToDictionary(cell => cell.Column, cell => cell.Json, StringComparer.Ordinal);

        public string Text(string column)
        {
            if (!json.TryGetValue(column, out var literal)) { return ""; }
            using var document = JsonDocument.Parse(literal);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : "";
        }

        public double Number(string column)
        {
            if (!json.TryGetValue(column, out var literal)) { return 0; }
            using var document = JsonDocument.Parse(literal);
            return document.RootElement.ValueKind == JsonValueKind.Number ? document.RootElement.GetDouble() : 0;
        }

        public DateTimeOffset? Time(string column)
            => DateTimeOffset.TryParse(Text(column), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

        public T? Json<T>(string column)
            => json.TryGetValue(column, out var literal) && literal != "null" ? JsonSerializer.Deserialize<T>(literal) : default;
    }

    // Runs: a synapse's recent successful run times, kept per program hash by runs.cs.
    public static TableValue[] RunKey(string behavior) => [new("behavior", JsonSerializer.Serialize(behavior))];

    public static (string Hash, Sample[] Recent) ParseRuns(TableValue[]? cells)
    {
        if (cells is null) { return ("", []); }
        var cell = new Cells(cells);
        return (cell.Text("hash"), cell.Json<Sample[]>("recent") ?? []);
    }

    public static TableValue[] RunValues(string hash, Sample[] recent) =>
    [
        new("hash", JsonSerializer.Serialize(hash)), new("recent", JsonSerializer.Serialize(recent)),
        new("p50", Median(recent).ToString(CultureInfo.InvariantCulture)), new("updated_at", Time(DateTimeOffset.UtcNow)),
    ];

    static long Median(Sample[] recent)
    {
        if (recent.Length == 0) { return 0; }
        var sorted = recent.Select(sample => sample.Microseconds).Order().ToArray();
        return sorted[(sorted.Length - 1) / 2];
    }

    // Each synapse's p50 and how many runs it is taken over; empty when runs.cs has recorded nothing yet.
    public static async Task<Dictionary<string, (long P50, int Samples)>> Medians(ITable runs, CancellationToken cancellationToken)
    {
        var medians = new Dictionary<string, (long P50, int Samples)>(StringComparer.Ordinal);
        foreach (var cells in await Pages(runs, cancellationToken))
        {
            var cell = new Cells(cells);
            var recent = cell.Json<Sample[]>("recent") ?? [];
            medians[cell.Text("behavior")] = ((long)cell.Number("p50"), recent.Length);
        }
        return medians;
    }

    public static Dictionary<string, int> Terms(string text)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Words(text ?? ""))
        {
            var term = Stem(word.ToLowerInvariant());
            if (term.Length < 2 || term.All(char.IsDigit) || Stop.Contains(term)) { continue; }
            terms[term] = terms.GetValueOrDefault(term) + 1;
        }
        return terms;
    }

    static Dictionary<string, int> Weigh(params (string Text, int Weight)[] parts)
    {
        var all = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (text, weight) in parts)
        {
            foreach (var (term, count) in Terms(text)) { all[term] = all.GetValueOrDefault(term) + count * weight; }
        }
        return all.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(MaxTerms)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    // Words split at anything but letters and digits, and inside identifiers: "ConnectAsync" is connect, async.
    static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        for (var index = 0; index <= text.Length; index++)
        {
            var current = index < text.Length ? text[index] : ' ';
            if (!char.IsLetterOrDigit(current))
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                continue;
            }
            var next = index + 1 < text.Length ? text[index + 1] : ' ';
            if (word.Length > 0 && Boundary(word[word.Length - 1], current, next)) { yield return word.ToString(); word.Clear(); }
            word.Append(current);
        }
    }

    static bool Boundary(char previous, char current, char next)
        => (char.IsLower(previous) && char.IsUpper(current))
            || (char.IsUpper(previous) && char.IsUpper(current) && char.IsLower(next))
            || char.IsDigit(previous) != char.IsDigit(current);

    // Plurals only: "words" and "word" are one term; "status" and "class" keep their ending.
    static string Stem(string term)
        => term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal) ? term[..^3] + "y"
            : term.Length > 3 && term[^1] == 's' && term[^2] is not ('s' or 'u' or 'i') ? term[..^1]
            : term;

    // tf-idf over the question's terms, scaled by how many of them a row holds, so a row with every
    // word beats one that repeats a single word. A term no row holds matches the terms it starts
    // ("calc" finds calculator) at half weight.
    static Dictionary<string, double> WordScores(IReadOnlyList<Row> rows, string question)
    {
        var wanted = Terms(question).Keys.ToArray();
        if (wanted.Length == 0 || rows.Count == 0) { return []; }
        var exact = wanted.ToDictionary(term => term, term => rows.Any(row => row.Terms.ContainsKey(term)), StringComparer.Ordinal);
        double Match(Row row, string term)
        {
            if (row.Terms.TryGetValue(term, out var count)) { return 1 + Math.Log(count); }
            if (exact[term] || term.Length < 3) { return 0; }
            var prefixed = row.Terms.Where(pair => pair.Key.StartsWith(term, StringComparison.Ordinal)).Select(pair => pair.Value).DefaultIfEmpty(0).Max();
            return prefixed == 0 ? 0 : (1 + Math.Log(prefixed)) / 2;
        }
        var idf = wanted.ToDictionary(term => term,
            term => Math.Log(1 + (double)rows.Count / (1 + rows.Count(row => Match(row, term) > 0))), StringComparer.Ordinal);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var matches = wanted.Select(term => Match(row, term) * idf[term]).ToArray();
            var held = matches.Count(match => match > 0);
            if (held > 0) { scores[row.Id] = matches.Sum() * held / wanted.Length; }
        }
        return scores;
    }

    // A row is near a question when its similarity stands out from the brain's median row by at least
    // NearLift and by half the best row's lift, so a question nothing is about leaves no row near. Near rows
    // score by their lift relative to the best row's.
    public sealed record Spread(double Median, double Best, double Floor);

    public static Spread? Measure(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, double>? meanings)
    {
        if (meanings is null) { return null; }
        var similarities = rows.Where(row => meanings.ContainsKey(row.Id)).Select(row => meanings[row.Id]).Order().ToArray();
        if (similarities.Length == 0) { return null; }
        var median = similarities[similarities.Length / 2];
        var best = similarities[^1];
        return new(median, best, median + Math.Max(NearLift, (best - median) / 2));
    }

    static Dictionary<string, double> Near(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, double> meanings)
    {
        if (Measure(rows, meanings) is not { } spread) { return []; }
        return rows.Where(row => meanings.TryGetValue(row.Id, out var similarity) && similarity >= spread.Floor)
            .ToDictionary(row => row.Id, row => (meanings[row.Id] - spread.Median) / (spread.Best - spread.Median), StringComparer.Ordinal);
    }

    // Words and meaning each score 0..1 against the best row; without meaning the words rank alone. Runs
    // nudge the score toward the synapse that ran more.
    public static Ranked[] Rank(IReadOnlyList<Row> rows, string question, IReadOnlyDictionary<string, double>? meanings)
    {
        var words = WordScores(rows, question);
        var top = words.Values.DefaultIfEmpty(0).Max();
        Dictionary<string, double> near = meanings is null ? [] : Near(rows, meanings);
        return [.. rows.Select(row =>
            {
                var word = top > 0 ? words.GetValueOrDefault(row.Id) / top : 0;
                var relevance = meanings is null ? word : WordsWeight * word + (1 - WordsWeight) * near.GetValueOrDefault(row.Id);
                double? similarity = meanings is not null && meanings.TryGetValue(row.Id, out var value) ? value : null;
                return new Ranked(row, relevance * (1 + 0.1 * Math.Log(1 + Math.Max(0, row.Runs))), word, similarity);
            })
            .Where(ranked => ranked.Score > 0)
            .OrderByDescending(ranked => ranked.Score).ThenBy(ranked => ranked.Row.Id, StringComparer.Ordinal)];
    }

    public static string RenderRecall(string question, IReadOnlyList<Row> rows, IReadOnlyDictionary<string, double>? meanings,
        string? note, Generation? generation)
    {
        var builder = new StringBuilder("# Recall: ").Append(question.Trim()).Append("\n\n");
        if (note is { Length: > 0 }) { builder.Append('_').Append(note).Append("_\n\n"); }
        if (Terms(question).Count == 0) { return builder.Append("Give recall some words to look for, such as what the synapse should do.").ToString(); }
        if (rows.Count == 0) { return builder.Append("Nothing is indexed: this brain has no other apps yet.").ToString(); }
        var ranked = Rank(rows, question, meanings);
        var by = meanings is null ? "words" : "meaning and words";
        if (ranked.Length == 0)
        {
            builder.Append("No synapse or app ").Append(meanings is null ? "holds these words" : "holds these words or is near them in meaning")
                .Append(". ").Append(rows.Count).Append(" rows are indexed.");
            if (Measure(rows, meanings) is { } far)
            {
                builder.Append(" The nearest is ").Append(far.Best.ToString("0.00", CultureInfo.InvariantCulture)).Append(", under the ")
                    .Append(far.Floor.ToString("0.00", CultureInfo.InvariantCulture)).Append(" near takes (median ")
                    .Append(far.Median.ToString("0.00", CultureInfo.InvariantCulture)).Append(").");
            }
            return builder.ToString();
        }
        Section(builder, "Synapses", ranked.Where(item => item.Row.Kind == "synapse").Take(ShownSynapses));
        Section(builder, "Apps", ranked.Where(item => item.Row.Kind == "spec").Take(ShownOthers));
        Section(builder, "Shared code", ranked.Where(item => item.Row.Kind == "shared").Take(ShownOthers));
        builder.Append("Ranked by ").Append(by).Append(" over ").Append(rows.Count).Append(" indexed rows");
        if (generation is not null) { builder.Append(" (generation ").Append(generation.Number).Append(')'); }
        builder.Append('.');
        if (Measure(rows, meanings) is { } spread)
        {
            builder.Append(" Near in meaning: similarity ").Append(spread.Floor.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" or more (median ").Append(spread.Median.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(", best ").Append(spread.Best.ToString("0.00", CultureInfo.InvariantCulture)).Append(").");
        }
        return builder.ToString();
    }

    static void Section(StringBuilder builder, string title, IEnumerable<Ranked> items)
    {
        var shown = items.ToArray();
        if (shown.Length == 0) { return; }
        builder.Append("## ").Append(title).Append("\n\n");
        for (var index = 0; index < shown.Length; index++)
        {
            var item = shown[index];
            builder.Append(index + 1).Append(". `").Append(item.Row.Id).Append("` · ").Append(item.Row.Title).Append(" v").Append(item.Row.Version)
                .Append(" · score ").Append(item.Score.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" (words ").Append(item.Words.ToString("0.00", CultureInfo.InvariantCulture));
            if (item.Meaning is { } meaning) { builder.Append(" · meaning ").Append(meaning.ToString("0.00", CultureInfo.InvariantCulture)); }
            builder.Append(')');
            if (item.Row.Kind == "synapse") { builder.Append(" · ").Append(item.Row.Runs).Append(item.Row.Runs == 1 ? " run" : " runs"); }
            if (item.Row.Excerpt.Length > 0) { builder.Append(" — ").Append(item.Row.Excerpt); }
            builder.Append('\n');
        }
        builder.Append('\n');
    }

    // The apps tree: each app, where it came from, and its files; each synapse with its status, the runs the
    // brain runtime counted since it started, its failures, its last run time and the p50 runs.cs keeps.
    public static string RenderApps(string scope, IReadOnlyList<(string Name, AppSnapshot State)> apps, BehaviorView[]? behaviors,
        IReadOnlyDictionary<string, (long P50, int Samples)> medians)
    {
        var body = new StringBuilder();
        int synapses = 0, running = 0;
        long runs = 0, failures = 0;
        foreach (var (name, state) in apps)
        {
            var key = IApp.Key(scope, name);
            body.Append("### ").Append(state.Manifest.Title).Append(" · `").Append(name).Append("` v").Append(state.Version)
                .Append(" · ").Append(state.Status).Append('\n').Append(Origin(state)).Append(" · ")
                .Append(state.Files.Count).Append(state.Files.Count == 1 ? " file" : " files").Append("\n\n");
            foreach (var (path, source) in state.Files.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                body.Append("- `").Append(path).Append("` · ");
                if (!AppFiles.IsProgram(path)) { body.Append("data\n"); continue; }
                if (!Shape.IsSynapse(source)) { body.Append("shared\n"); continue; }
                synapses++;
                var id = BehaviorIds.For(key, path);
                if (Find(behaviors, id) is not { } behavior)
                { body.Append(behaviors is null ? "synapse\n" : "synapse · not assigned\n"); continue; }
                var pending = behavior.Subscriptions.Sum(subscription => subscription.Pending);
                if (behavior.Status == "Running") { running++; }
                runs += behavior.Runs;
                failures += behavior.Failures;
                body.Append("synapse · ").Append(behavior.Status).Append(" · ").Append(behavior.Runs).Append(behavior.Runs == 1 ? " run" : " runs");
                if (behavior.Failures > 0) { body.Append(" · ").Append(behavior.Failures).Append(" failed"); }
                if (behavior.LastDurationMicroseconds > 0) { body.Append(" · last ").Append(Duration(behavior.LastDurationMicroseconds)); }
                if (medians.TryGetValue(id, out var median) && median.Samples > 0)
                { body.Append(" · p50 ").Append(Duration(median.P50)).Append(" over ").Append(median.Samples); }
                if (pending > 0) { body.Append(" · ").Append(pending).Append(" pending"); }
                if (behavior.Error is { Length: > 0 } error) { body.Append(" · ").Append(Excerpt(error)); }
                body.Append('\n');
            }
            body.Append('\n');
        }
        var header = new StringBuilder("## Apps\n\n").Append(apps.Count).Append(" apps · ").Append(synapses).Append(" synapses · ");
        header.Append(behaviors is null ? "behavior status unavailable: the brain runtime did not answer"
            : $"{running} running · {runs} runs · {failures} failed since the brain runtime started");
        return header.Append("\n\n").Append(body).ToString();
    }

    static string Duration(long microseconds)
        => microseconds >= 1_000_000 ? (microseconds / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : microseconds >= 1000 ? (microseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " ms"
            : microseconds.ToString(CultureInfo.InvariantCulture) + " µs";

    static string Origin(AppSnapshot state)
        => state.Origin is not { } origin ? "Made in this brain"
            : "From " + origin.Publisher + (origin.Source is AppSource.Shipped ? " (shipped)" : " (a brain)") + " v" + origin.Version
                + (state.Edited ? ", edited here" : "");

    // The installed types by provider: a module id, or feature:{owner}/{name}.
    public static string RenderTypes(IReadOnlyList<TypeEntry> types)
    {
        var builder = new StringBuilder("## Installed types\n\n").Append(types.Count).Append(" types: ")
            .Append(types.Count(type => type.Kind == TypeKind.Neuron)).Append(" neurons, ")
            .Append(types.Count(type => type.Kind == TypeKind.Signal)).Append(" signals, ")
            .Append(types.Count(type => type.Kind == TypeKind.Connector)).Append(" connectors.\n\n");
        foreach (var provider in types.GroupBy(type => type.Provider).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var neurons = provider.Where(type => type.Kind == TypeKind.Neuron).Select(type => type.Name[(type.Name.LastIndexOf('.') + 1)..])
                .Order(StringComparer.Ordinal).ToArray();
            builder.Append("- **").Append(provider.Key).Append("**: ").Append(neurons.Length).Append(" neurons, ")
                .Append(provider.Count(type => type.Kind == TypeKind.Signal)).Append(" signals, ")
                .Append(provider.Count(type => type.Kind == TypeKind.Connector)).Append(" connectors");
            if (neurons.Length > 0) { builder.Append(" — ").Append(string.Join(", ", neurons)); }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // A file's own description: its first comment block.
    static string Summary(string source)
        => Excerpt(string.Join(" ", source.Split('\n').Take(40).Select(line => line.Trim())
            .SkipWhile(line => !line.StartsWith("//", StringComparison.Ordinal))
            .TakeWhile(line => line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.TrimStart('/').Trim())));

    public static string Excerpt(string text)
    {
        var flat = string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= ExcerptLength ? flat : flat[..ExcerptLength].TrimEnd() + "…";
    }
}
