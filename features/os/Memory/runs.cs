using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Postgres.Tables;

// "runs" keeps each synapse's median run time: every successful run of this brain's synapses adds its
// duration to that synapse's recent runs in the {app}/runs table, and the apps view reads the p50. A run
// redelivered with the same subscription and watermark is already there and changes nothing; a new program
// hash starts the synapse's runs again. Failed and dropped runs carry no duration; the runtime counts them.
// This synapse is the table's only writer, so what it wrote is cached and a row is read once per process:
// one table call per run, not two.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var table = brain.Get<ITable>(Memory.RunsKey(appKey));
var written = new Dictionary<string, (string Hash, Memory.Sample[] Recent)>(StringComparer.Ordinal);
var defined = false;

await foreach (var ran in brain.On<BehaviorRan>(brain.Get<IBrainRuns>(BrainRunsKeys.For(Memory.Scope(brain))), brain.Stopping))
{
    if (ran.Outcome != RunOutcome.Succeeded || ran.DurationMicroseconds <= 0) { continue; }
    var key = Memory.RunKey(ran.Behavior);
    try
    {
        if (!defined) { await table.Define(Memory.RunsDefinition); defined = true; }
        if (!written.TryGetValue(ran.Behavior, out var known)) { known = Memory.ParseRuns(await table.Read(key)); }
        Memory.Sample[] recent = known.Hash == ran.Hash ? known.Recent : [];
        var mark = $"{ran.Source}|{ran.SignalType}|{ran.Watermark}";
        if (recent.Any(sample => sample.Mark == mark)) { written[ran.Behavior] = known; continue; }
        Memory.Sample[] next = [.. recent.TakeLast(Memory.RecentRuns - 1), new Memory.Sample(mark, ran.DurationMicroseconds)];
        await table.Upsert(key, Memory.RunValues(ran.Hash, next));
        written[ran.Behavior] = (ran.Hash, next);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        written.Remove(ran.Behavior);
        Console.Error.WriteLine($"The run of {ran.Behavior} through {ran.Watermark} was not recorded: {error.Message}");
    }
}
