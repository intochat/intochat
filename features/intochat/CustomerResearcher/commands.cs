using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;

// The queue's commands. Research and stop share one request stream because their order is the meaning:
// a stop cancels every research requested before it, however far behind any other synapse is. This
// synapse is the only writer of requests and cancellations; a command delivered again (at-least-once) is
// recognized by its request id and applied once. worker.cs runs the queue.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var requests = ResearchState.Requests(brain, key);
var cancellations = ResearchState.Cancellations(brain, key);
var outcomes = ResearchState.Outcomes(brain, key);
var active = ResearchState.Active(brain, key);
var status = ResearchState.Status(brain, key);

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation is not ("research" or "stop")) { continue; }
    var id = request.RequestId.ToString("N");
    var queued = await ResearchState.ReadRequests(requests);
    var cancelled = await ResearchState.ReadCancellations(cancellations);
    var settled = (await ResearchState.ReadOutcomes(outcomes)).Select(item => item.Id).ToHashSet();
    if (request.Operation == "research")
    {
        var query = request.Input.Trim();
        if (query.Length is < 1 or > 500)
        {
            await status.Set("Name a company, location or website to research (at most 500 characters).");
            continue;
        }
        if (queued.Any(item => item.Id == id)) { continue; }
        // Keep every open request and the most recent settled ones.
        var marker = (await active.Read()).Markdown;
        var kept = queued.Where((item, index) => index >= queued.Length - 99 || ResearchState.IsOpen(item, settled, cancelled.Ids, marker));
        await ResearchState.Write(requests, kept.Append(new ResearchRequest(id, query)).ToArray());
    }
    else
    {
        if (cancelled.Stops.Contains(id))
        {
            // Applied already. Only a stop whose status was never written (this synapse was reaped between the
            // two) is answered again; it is the last stop, so nothing requested after it is queued yet.
            if (cancelled.Stops[^1] != id || cancelled.Answered == id) { continue; }
        }
        else
        {
            // Waiting requests never start; the running one stops at its next browser or model step.
            var ids = queued.Select(item => item.Id).ToArray();
            var open = cancelled.Ids.Intersect(ids).Union(ids.Where(item => !settled.Contains(item))).ToArray();
            cancelled = new ResearchCancellations(open, [.. cancelled.Stops.TakeLast(ResearchState.StopMemory - 1), id], cancelled.Answered);
            await ResearchState.Write(cancellations, cancelled);
        }
        // The cancellations are persisted; now the status answers.
        var running = (await active.Read()).Markdown;
        await status.Set(running.Length > 0 ? "Stopping…" : "Stopped");
        // The run may have settled meanwhile. The worker records an outcome before it writes its status, so an
        // outcome read after "Stopping…" was written catches every settle that write could have hidden.
        var recorded = await ResearchState.ReadOutcomes(outcomes);
        if (running.Length > 0 && recorded.Any(item => item.Id == running)) { await status.Set(ResearchState.Display(recorded[^1])); }
        await ResearchState.Write(cancellations, cancelled with { Answered = id });
    }
}
