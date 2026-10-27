using IntoChat.OS.Modules.AI.Embeddings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Postgres.Tables;

// "recall" ranks the current generation of the index against the question, by meaning and words: the
// question is embedded and the vector index's nearest rows are merged with the words' rank. When the
// question cannot be embedded, or the generation has no vectors, the words rank alone and the answer says
// so. Runs are what the brain runtime counts now. The answer is written under the request id and as the
// latest answer the surface shows; a request whose answer is already written was answered and is skipped,
// so a redelivered request never replaces a newer latest answer. An empty or unreadable index is answered
// from the installed apps by words, and a reindex is requested.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var rows = brain.Get<ITable>(Memory.RowsKey(appKey));
var generations = brain.Get<ITable>(Memory.GenerationsKey(appKey));
var scope = Memory.Scope(brain);
var vectors = brain.Get<IVectorIndex>(Memory.VectorsKey(scope));

await foreach (var request in brain.On(app, "recall", brain.Stopping))
{
    var answered = brain.Get<IText>(appKey + "/recalls/" + request.RequestId);
    string answer;
    try
    {
        if ((await answered.Read()).Markdown.Length > 0) { continue; }
        if (await Indexed() is { Rows.Count: > 0 } index)
        {
            var (meanings, note) = await Meanings(index, request.Input);
            var ranked = Memory.WithRuns(scope, index.Rows, await Memory.Behaviors(brain));
            answer = Memory.RenderRecall(request.Input, ranked, meanings, note, index.Generation);
        }
        else
        {
            var live = await Memory.Collect(brain);
            await app.Request("reindex", "");
            answer = Memory.RenderRecall(request.Input, live, null,
                "Answered from the installed apps by words: the index was empty or unreadable, so a reindex was requested.", null);
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { answer = "# Recall: " + request.Input.Trim() + "\n\nMemory could not answer: " + error.Message; }
    await answered.Set(answer);
    await brain.Get<IText>(appKey + "/recall").Set(answer);
}

async Task<Memory.Index?> Indexed()
{
    try
    {
        await rows.Define(Memory.RowsDefinition);
        await generations.Define(Memory.GenerationsDefinition);
        return await Memory.ReadIndex(generations, rows, brain.Stopping);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return null; }
}

async Task<(IReadOnlyDictionary<string, double>? Meanings, string? Note)> Meanings(Memory.Index index, string question)
{
    if (Memory.Terms(question).Count == 0) { return (null, null); }
    if (index.Generation.Vectors == 0)
    { return (null, "Ranked by words only: this generation has no meaning vectors" + (index.Generation.Note.Length > 0 ? " (" + index.Generation.Note + ")." : ".")); }
    try
    {
        var query = (await Memory.Embed(brain, [question]))[0];
        var nearest = await vectors.Nearest(query, index.Generation.Vectors);
        return (nearest.ToDictionary(match => match.Id, match => match.Score, StringComparer.Ordinal), null);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { return (null, "Ranked by words only: the question could not be embedded (" + Memory.Excerpt(error.Message) + ")."); }
}
