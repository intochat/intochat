using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Facts;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Time.Reminders;
using IntoChat.OS.Time.Reminders.Signals;

// Consolidation turns episodes into facts, review items or nothing. "capture" restarts the reminder
// {app}/consolidation after every episode, so it ticks once the brain has been quiet for QuietMinutes; on the tick
// (and once at startup) the reminder is stopped and batches run until no episode is left to claim.
// A batch claims up to 20 unconsolidated episodes (claim = run id, claim_until = now + 5 minutes; an expired claim
// is claimable again), asks Memory's consolidate agent (prompt consolidate.md, ConsolidateModel, no tools, 60 seconds)
// for a verdict and classifies every item (Consolidation.Classify: secrets are dropped, sensitive or unsure items go to
// review). The parsed verdict is stored in {app}/verdicts under the batch id (the claim) before anything is applied, so
// a batch re-run after a crash replays that verdict without asking the model again; every item is keyed by
// Consolidation.DedupKey (a hash of its normalised kind, subject, text and episode ids), so a replay asserts,
// reviews and declares nothing twice; a contradicted current fact is invalidated as superseded; working items replace
// {app}/working; intent items are declared. Review items go to {app}/review-items. Episodes are marked consolidated
// last. A failed batch leaves its claims to expire and arms the reminder to try again after they do.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var episodes = brain.Get<ITable>(Episodes.TableKey(appKey));
var reviews = brain.Get<ITable>(Consolidation.ReviewTableKey(appKey));
var archive = brain.Get<ITable>(Consolidation.ArchiveKey(appKey));
var verdicts = brain.Get<ITable>(Consolidation.VerdictsKey(appKey));
var agentKey = Consolidation.AgentKey(appKey);
var agent = brain.Get<IAgent>(agentKey);
var facts = brain.Get<IFacts>(IFacts.Key(Memory.Scope(brain)));
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
var working = brain.Get<IText>(Intents.WorkingKey(appKey));
var status = brain.Get<IText>(appKey + "/status");
var reminder = brain.Get<IReminder>(Consolidation.ReminderKey(appKey));
var defined = false;

TableDefinition reviewDefinition = new(
[
    new("id", "text"), new("episode_ids", "text"), new("kind", "text"), new("text", "text"), new("subject", "text"),
    new("confidence", "number"), new("importance", "number"), new("reason", "text"), new("contradicts", "text"),
    new("at", "timestamptz"), new("status", "text"),
], ["id"]);
TableDefinition verdictDefinition = new(
[
    new("id", "text"), new("episode_ids", "text"), new("items", "text"), new("prompt_version", "text"), new("reflect", "boolean"),
    new("at", "timestamptz"),
], ["id"]);
TableDefinition archiveDefinition = new(
[
    new("id", "text"), new("kind", "text"), new("text", "text"), new("subject", "text"), new("confidence", "number"),
    new("importance", "number"), new("valid_from", "timestamptz"), new("archived_at", "timestamptz"),
], ["id"]);

// The startup drain runs once the tick subscription is live, so a tick during it is not lost.
await foreach (var _ in brain.On<ReminderTick>(reminder, Drain, brain.Stopping))
{
    try { await reminder.Stop(); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Report("consolidation could not stop its reminder: " + error.Message); }
    await Drain();
}

// Batches until one claims nothing; a failed batch stops the drain and is retried once its claims expired.
async Task Drain()
{
    try
    {
        if (!defined)
        {
            await episodes.Define(Episodes.Definition);
            await reviews.Define(reviewDefinition);
            await archive.Define(archiveDefinition);
            await verdicts.Define(verdictDefinition);
            defined = true;
        }
        while (await Batch()) { }
        await RearmForClaims();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await Report("consolidation failed: " + error.Message);
        try { await reminder.Start(Consolidation.ClaimFor + TimeSpan.FromSeconds(30), TimeSpan.FromDays(365)); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}

// Claims are written without compare-and-swap: one consolidate process runs its batches one after another, so no
// other writer can claim the same episode; the expiry only covers a crash of this process.
async Task<bool> Batch()
{
    var now = DateTimeOffset.UtcNow;
    var all = (await ReadAll(episodes)).Where(row => row.Time("consolidated_at") is null)
        .OrderBy(row => row.Time("at") ?? DateTimeOffset.MaxValue).ToArray();
    var stored = (await ReadAll(verdicts)).ToDictionary(row => row.Text("id"), StringComparer.Ordinal);
    var plan = Consolidation.Next(
        [.. all.Select(row => new Consolidation.Candidate(row.Text("id"), row.Text("claim"),
            Consolidation.Claimable(null, row.Text("claim"), row.Time("claim_until"), now)))],
        stored.Keys.ToHashSet(StringComparer.Ordinal), "consolidate-" + Guid.NewGuid().ToString("N"));
    if (plan is null) { return false; }
    var ids = plan.EpisodeIds.ToHashSet(StringComparer.Ordinal);
    var rows = all.Where(row => ids.Contains(row.Text("id"))).ToArray();
    foreach (var row in rows)
    {
        await Write(episodes, row, new()
        {
            ["claim"] = JsonSerializer.Serialize(plan.BatchId),
            ["claim_until"] = JsonSerializer.Serialize((now + Consolidation.ClaimFor).ToString("O")),
        });
    }

    var snapshot = await app.Read();
    var gate = Consolidation.Gate(snapshot.Settings.GetValueOrDefault("ReviewGate", "0.75"));
    Consolidation.Verdict[] items;
    // The episodes the verdict was given: on a replay, those stored with it, so an item keeps the episode ids (and
    // the dedup key) it had the first time even when some of its episodes were already marked consolidated.
    var given = ids;
    string version;
    bool reflect;
    if (plan.Replay)
    {
        var verdict = stored[plan.BatchId];
        items = JsonSerializer.Deserialize<Consolidation.Verdict[]>(verdict.Text("items")) ?? [];
        version = verdict.Text("prompt_version");
        reflect = Consolidation.Flag(verdict.Cells.GetValueOrDefault("reflect"));
        given = verdict.Text("episode_ids").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
    else
    {
        var prompt = snapshot.Files.GetValueOrDefault("prompts/consolidate.md", "");
        var model = snapshot.Settings.GetValueOrDefault("ConsolidateModel", "");
        reflect = Consolidation.Reflect(rows.Select(row => (int)row.Number("importance")));
        var current = await facts.Current();
        var input = JsonSerializer.Serialize(new
        {
            episodes = rows.Select(row => new { id = row.Text("id"), at = row.Time("at"), importance = (int)row.Number("importance"), summary = row.Text("summary") }),
            facts = string.Join("\n", current.Select(fact => fact.Id + " | " + fact.Kind.ToString().ToLowerInvariant() + " | " + fact.Subject + " | " + fact.Text)),
            working = (await working.Read()).Markdown,
            reflect,
        });
        items = Consolidation.ParseVerdict(await Ask(plan.BatchId, input, model, prompt))
            ?? throw new InvalidOperationException("the consolidate agent's reply was not the expected JSON");
        version = Consolidation.PromptVersion(prompt);
        // Stored before anything is applied: a re-run of this batch replays exactly this verdict.
        await verdicts.Upsert([new("id", JsonSerializer.Serialize(plan.BatchId))],
        [
            new("episode_ids", JsonSerializer.Serialize(string.Join(",", plan.EpisodeIds))),
            new("items", JsonSerializer.Serialize(JsonSerializer.Serialize(items))),
            new("prompt_version", JsonSerializer.Serialize(version)), new("reflect", reflect ? "true" : "false"),
            new("at", JsonSerializer.Serialize(now.ToString("O"))),
        ]);
    }

    var insights = 0;
    foreach (var original in items)
    {
        // Only episodes of this batch count as evidence or origin.
        var item = original with { EpisodeIds = [.. original.EpisodeIds.Where(given.Contains)] };
        // Already applied when every episode it came from was marked consolidated before a crash.
        if (item.EpisodeIds.Length == 0 || !item.EpisodeIds.Any(ids.Contains)) { continue; }
        if (item.Kind == "insight" && (!reflect || ++insights > Consolidation.MaxInsights)) { continue; }
        var decision = Consolidation.Classify(item, gate, Consolidation.SensitiveClasses);
        var dedup = Consolidation.DedupKey(item);
        switch (decision.Disposition)
        {
            case Consolidation.Disposition.Publish: await Publish(item, dedup, version); break;
            case Consolidation.Disposition.Review: await Review(item, dedup, decision.Reason, now); break;
            case Consolidation.Disposition.Discard: break;
        }
    }

    // Last: an episode is consolidated only once everything it produced is written.
    var done = JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("O"));
    foreach (var row in rows)
    {
        await Write(episodes, row, new() { ["consolidated_at"] = done, ["claim"] = "null", ["claim_until"] = "null" });
    }
    return true;
}

// Episodes still held by an unexpired claim (a batch of a run that stopped) are retried once the claim expires.
async Task RearmForClaims()
{
    var now = DateTimeOffset.UtcNow;
    var held = (await ReadAll(episodes))
        .Where(row => row.Time("consolidated_at") is null && row.Text("claim").Length > 0 && row.Time("claim_until") > now)
        .Select(row => row.Time("claim_until")!.Value).ToArray();
    if (held.Length == 0) { return; }
    await reminder.Start(held.Min() - now + TimeSpan.FromSeconds(5), TimeSpan.FromDays(365));
}

async Task Publish(Consolidation.Verdict item, string dedup, string version)
{
    switch (item.Kind)
    {
        case "working":
            await working.Set(Intents.RenderWorking(item.Text));
            return;
        case "intent":
            await intents.Declare(new(Consolidation.IntentRequestId(item), item.Text, ""));
            return;
    }
    var kind = item.Kind switch { "profile" => FactKind.Profile, "insight" => FactKind.Insight, _ => FactKind.Fact };
    var text = kind == FactKind.Insight ? Consolidation.WithEvidence(item.Text, item.EpisodeIds) : item.Text;
    if (text.Length > IFacts.TextLimit) { text = text[..IFacts.TextLimit]; }
    var subject = item.Subject.Length > IFacts.SubjectLimit ? item.Subject[..IFacts.SubjectLimit] : item.Subject;
    var assertion = new FactAssertion(kind, text, subject, item.Confidence, item.Importance, DateTimeOffset.UtcNow, agentKey, 0, version, [], dedup);
    Guid id;
    // Assert answers a known dedup key before it checks the cap, so a fact is archived only for a genuinely new one.
    try { id = await facts.Assert(assertion); }
    catch (InvalidOperationException full) when (Consolidation.IsCapRefusal(full))
    {
        await Retrying(Archive);
        id = await facts.Assert(assertion);
    }
    await Recall.Index(brain, "fact:" + id.ToString("D"), "fact", (item.Subject.Length > 0 ? item.Subject + ": " : "") + text, assertion.ValidFrom, item.Importance, kind.ToString().ToLowerInvariant());
    if (item.Contradicts is { } contradicts && Guid.TryParse(contradicts, out var old) && old != id)
    {
        await Retrying(async () =>
        {
            var snapshot = await facts.Read();
            if (!snapshot.Facts.Any(fact => fact.Id == old && fact.ValidTo is null && !fact.Hidden)) { return; }
            await facts.Invalidate(old, "superseded by " + id, id, snapshot.Revision);
        });
    }
}

// The least important current fact (oldest first among equals) is copied to the archive table and forgotten.
async Task Archive()
{
    var live = await facts.Current();
    if (live.Length < IFacts.Cap) { return; }
    var lowest = live.OrderBy(fact => fact.Importance).ThenBy(fact => fact.Recorded).First();
    await archive.Upsert([new("id", JsonSerializer.Serialize(lowest.Id.ToString("D")))],
    [
        new("kind", JsonSerializer.Serialize(lowest.Kind.ToString().ToLowerInvariant())), new("text", JsonSerializer.Serialize(lowest.Text)),
        new("subject", JsonSerializer.Serialize(lowest.Subject)), new("confidence", JsonSerializer.Serialize(lowest.Confidence)),
        new("importance", lowest.Importance.ToString()), new("valid_from", JsonSerializer.Serialize(lowest.ValidFrom.ToString("O"))),
        new("archived_at", JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("O"))),
    ]);
    await facts.Forget(lowest.Id, "archived", (await facts.Read()).Revision);
}

async Task Review(Consolidation.Verdict item, string dedup, string reason, DateTimeOffset at)
{
    await reviews.Upsert([new("id", JsonSerializer.Serialize(dedup))],
    [
        new("episode_ids", JsonSerializer.Serialize(string.Join(",", item.EpisodeIds))), new("kind", JsonSerializer.Serialize(item.Kind)),
        new("text", JsonSerializer.Serialize(item.Text)), new("subject", JsonSerializer.Serialize(item.Subject)),
        new("confidence", JsonSerializer.Serialize(item.Confidence)), new("importance", item.Importance.ToString()),
        new("reason", JsonSerializer.Serialize(reason)), new("contradicts", item.Contradicts is null ? "null" : JsonSerializer.Serialize(item.Contradicts)),
        new("at", JsonSerializer.Serialize(at.ToString("O"))), new("status", JsonSerializer.Serialize("pending")),
    ]);
}

// A write that meets a revision conflict is read again and retried, up to Intents.FireAttempts in all.
async Task Retrying(Func<Task> write)
{
    for (var attempt = 1; ; attempt++)
    {
        try { await write(); return; }
        catch (Exception error) when (Intents.ShouldRetry(error, attempt) && !brain.Stopping.IsCancellationRequested) { }
    }
}

// Every column is written back, so an update never depends on whether Upsert merges or replaces.
static async Task Write(ITable table, Row row, Dictionary<string, string> changes)
{
    var values = row.Cells.Where(cell => cell.Key != "id" && !changes.ContainsKey(cell.Key)).Select(cell => new TableValue(cell.Key, cell.Value))
        .Concat(changes.Select(change => new TableValue(change.Key, change.Value))).ToArray();
    await table.Upsert([new("id", row.Cells["id"])], values);
}

static async Task<List<Row>> ReadAll(ITable table)
{
    var rows = new List<Row>();
    for (var offset = 0; ; offset += 200)
    {
        var page = await table.Page(offset, 200);
        rows.AddRange(page.Select(cells => new Row(cells.ToDictionary(cell => cell.Column, cell => cell.Json, StringComparer.Ordinal))));
        if (page.Length < 200) { return rows; }
    }
}

async Task Report(string text)
{
    try { await status.Set(text.Length <= 200 ? text : text[..200]); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task<string> Ask(string runId, string user, string model, string prompt)
{
    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    waiting.CancelAfter(Consolidation.Ceiling);
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var failedLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // Both subscriptions are live before the turn starts, so a fast signal is never missed.
    var watchers = new[]
    {
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnFailed>(agent, () => { failedLive.TrySetResult(); return Task.CompletedTask; }, waiting.Token))
            { if (turn.RunId == runId) { done.TrySetException(new InvalidOperationException("the agent failed: " + turn.Error)); return; } }
        }),
        Task.Run(async () =>
        {
            await foreach (var turn in brain.On<TurnCompleted>(agent, async () =>
            {
                try
                {
                    await failedLive.Task.WaitAsync(waiting.Token);
                    // Each batch is judged on its own input, never on an earlier batch's transcript.
                    await agent.Clear();
                    await agent.Start(AgentTurn.Text(user, runId: runId, model: model.Length > 0 ? model : null,
                        instructions: prompt, contracts: []));
                }
                catch (Exception error) when (error is not OperationCanceledException) { done.TrySetException(error); }
            }, waiting.Token))
            { if (turn.RunId == runId) { done.TrySetResult(turn.Reply); return; } }
        }),
    };
    try { return await done.Task.WaitAsync(Consolidation.Ceiling, brain.Stopping); }
    catch (TimeoutException)
    {
        try { await agent.CancelRun(runId); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        throw new TimeoutException("the consolidate agent did not answer in 60 seconds");
    }
    finally
    {
        waiting.Cancel();
        try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
    }
}

// An episode row as the table stores it: column to JSON literal.
sealed record Row(Dictionary<string, string> Cells)
{
    public string Text(string column)
    {
        if (!Cells.TryGetValue(column, out var literal)) { return ""; }
        using var document = JsonDocument.Parse(literal);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : "";
    }

    public double Number(string column)
    {
        if (!Cells.TryGetValue(column, out var literal)) { return 0; }
        using var document = JsonDocument.Parse(literal);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Number => document.RootElement.GetDouble(),
            JsonValueKind.String when double.TryParse(document.RootElement.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    public DateTimeOffset? Time(string column)
        => Text(column) is { Length: > 0 } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
