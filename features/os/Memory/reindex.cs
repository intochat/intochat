using System.Globalization;
using DigitalBrain.AI.Embeddings;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Postgres.Tables;

// "reindex" rebuilds the index from scratch as a new generation: every row of the installed apps is written
// under it, the rows' meaning vectors replace the vector index in one call, and only then does the
// generation marker switch to it, so recall never reads a half-built index. The previous generation is
// kept for a recall that read the marker before the switch; older ones are deleted. A rebuild that never
// switched left rows past the marker, and the next rebuild deletes them first. When what the rows would
// store and embed is what the current complete generation holds, nothing is rebuilt: a redelivered or
// repeated request costs one read of the installed apps.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var rows = brain.Get<ITable>(Memory.RowsKey(appKey));
var generations = brain.Get<ITable>(Memory.GenerationsKey(appKey));
var vectors = brain.Get<IVectorIndex>(Memory.VectorsKey(Memory.Scope(brain)));
var status = brain.Get<IText>(appKey + "/status");

await foreach (var _ in brain.On(app, "reindex", brain.Stopping))
{
    try
    {
        await rows.Define(Memory.RowsDefinition);
        await generations.Define(Memory.GenerationsDefinition);
        var collected = await Memory.Collect(brain);
        var fingerprint = Memory.Fingerprint(collected);
        var current = await Memory.ReadGeneration(generations);
        if (current is { Note.Length: 0 } built && built.Fingerprint == fingerprint && built.Rows == collected.Length)
        {
            await status.Set($"Indexed {collected.Count(row => row.Kind == "spec")} apps: {collected.Count(row => row.Kind == "synapse")} synapses, " +
                $"{collected.Count(row => row.Kind == "shared")} shared files; {built.Vectors} meaning vectors; generation {built.Number} is current, " +
                $"nothing changed since {built.BuiltAt:HH:mm:ss} UTC. {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
            continue;
        }
        var number = (current?.Number ?? 0) + 1;
        var stored = await Memory.ReadAll(rows, brain.Stopping);
        foreach (var row in stored.Where(row => row.Generation >= number)) { await rows.Delete(Memory.Key(row)); }
        foreach (var row in collected) { await rows.Upsert(Memory.Key(row with { Generation = number }), Memory.Values(row)); }
        var (embedded, note) = await Embed(collected, number);
        await generations.Upsert(Memory.CurrentKey, Memory.Values(new Memory.Generation(number, collected.Length, embedded, note, DateTimeOffset.UtcNow, fingerprint)));
        var pruned = stored.Where(row => row.Generation < number - 1).ToArray();
        foreach (var row in pruned) { await rows.Delete(Memory.Key(row)); }
        await status.Set($"Indexed {collected.Count(row => row.Kind == "spec")} apps: {collected.Count(row => row.Kind == "synapse")} synapses, " +
            $"{collected.Count(row => row.Kind == "shared")} shared files; {embedded} meaning vectors" + (note.Length > 0 ? $" ({note})" : "") +
            $"; generation {number}, {pruned.Length} old rows removed. {DateTimeOffset.UtcNow:HH:mm:ss} UTC");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set("The index could not be rebuilt: " + error.Message); }
}

// Synapses are embedded first when the rows outgrow the vector index; a model that fails leaves the
// generation without vectors and recall ranks it by words.
async Task<(int Embedded, string Note)> Embed(Memory.Row[] collected, long number)
{
    const int capacity = 1024;
    var chosen = collected.OrderBy(row => row.Kind == "synapse" ? 0 : row.Kind == "spec" ? 1 : 2).Take(capacity).ToArray();
    try
    {
        var embedded = await Memory.Embed(brain, [.. chosen.Select(row => row.Meaning)]);
        var generation = number.ToString(CultureInfo.InvariantCulture);
        await vectors.Replace([.. chosen.Select((row, index) => new VectorEntry(row.Id, embedded[index], new()
        {
            ["kind"] = row.Kind, ["app"] = row.App, ["path"] = row.Path, ["generation"] = generation,
        }))]);
        return (chosen.Length, chosen.Length < collected.Length ? $"{collected.Length - chosen.Length} rows past the vector index's {capacity} entries have no vector" : "");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { return (0, "the rows could not be embedded: " + Memory.Excerpt(error.Message)); }
}
