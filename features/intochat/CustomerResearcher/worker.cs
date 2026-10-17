using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// The queue's runner. Every new request, and the start itself (which resumes research a reaped run left
// unfinished), drains the queue in order: one company at a time, each settled as an outcome. This synapse
// alone writes outcomes and the active marker. A stop takes effect at the run's next browser or model step,
// and a run a restart interrupted is finished, settled as stopped, or researched again on the next start.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var requests = ResearchState.Requests(brain, key);
var cancellations = ResearchState.Cancellations(brain, key);
var outcomes = ResearchState.Outcomes(brain, key);
var active = ResearchState.Active(brain, key);
var status = ResearchState.Status(brain, key);

await brain.React<TextChanged>(requests, Drain, brain.Stopping);

async Task Drain(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var queued = await ResearchState.ReadRequests(requests);
        var cancelled = (await ResearchState.ReadCancellations(cancellations)).Ids;
        var recorded = await ResearchState.ReadOutcomes(outcomes);
        var settled = recorded.Select(item => item.Id).ToHashSet();
        var marker = (await active.Read()).Markdown;
        if (marker.Length > 0)
        {
            // A settle a reaped process interrupted: the outcome is recorded, its status or cleared marker is not.
            if (recorded.LastOrDefault(item => item.Id == marker) is { } done) { await Finish(done); continue; }
            // A run a reaped process left unfinished, and a stop cancelled meanwhile, settles as stopped.
            if (cancelled.Contains(marker)) { await Settle(new ResearchOutcome(marker, "stopped", "Stopped.")); continue; }
        }
        // An unfinished run that was not stopped is the first open request, so it is researched again.
        var next = queued.FirstOrDefault(item => !settled.Contains(item.Id) && !cancelled.Contains(item.Id));
        if (next is null)
        {
            if (marker.Length > 0) { await active.Set(""); }
            return;
        }
        // A stop applied since this pass read the queue: the request never starts.
        if ((await ResearchState.ReadCancellations(cancellations)).Ids.Contains(next.Id)) { continue; }
        // No outcome means the behavior is stopping: the request stays pending for the next start to resume.
        if (await Run(next, ct) is not { } outcome) { return; }
        await Settle(outcome);
    }
}

async Task<ResearchOutcome?> Run(ResearchRequest entry, CancellationToken ct)
{
    async Task Checkpoint()
    {
        ct.ThrowIfCancellationRequested();
        if ((await ResearchState.ReadCancellations(cancellations)).Ids.Contains(entry.Id)) { throw new ResearchStopped(); }
    }
    async Task Progress(string text)
    {
        await Checkpoint();
        await status.Set(text);
    }
    try
    {
        await active.Set(entry.Id);
        await Progress("Researching " + entry.Query + "…");
        var (company, verdict) = await Researcher.Research(brain, key, entry.Query, Progress, Checkpoint, ct);
        if (company is null) { return new(entry.Id, "failed", verdict); }
        // The last checkpoint: a stop after it loses the race to a whole save, never a half one.
        await Progress("Saving…");
        await ResearchStore.Save(brain, key, entry.Query, company);
        await brain.Get<IText>(key + "/result/" + entry.Id).Set(await ResearchStore.Row(brain, key, entry.Query));
        await ResearchView.Saved(brain, key, entry.Query, company);
        return new(entry.Id, "completed", "Saved: " + company.CompanyName);
    }
    catch (ResearchStopped) { return new(entry.Id, "stopped", "Stopped."); }
    catch (Exception) when (ct.IsCancellationRequested) { return null; }
    catch (OperationCanceledException) { return new(entry.Id, "failed", "Research timed out. Retry to safely update the same record."); }
    catch (ScriptRefusalException error) { return new(entry.Id, "failed", error.Message); }
    catch (ResearchFailure error) { return new(entry.Id, "failed", error.Message); }
    catch (Exception) { return new(entry.Id, "failed", "Research or save failed. Retry to safely update the same record."); }
}

// The outcome is recorded first, then its status, then the marker is cleared: commands.cs relies on the outcome
// preceding the status when a stop races a run, and a marker that names a recorded outcome tells a restarted
// worker that the settle was interrupted.
async Task Settle(ResearchOutcome outcome)
{
    var ids = (await ResearchState.ReadRequests(requests)).Select(item => item.Id).ToHashSet();
    var recorded = await ResearchState.ReadOutcomes(outcomes);
    await ResearchState.Write(outcomes, recorded.Where(item => item.Id != outcome.Id && ids.Contains(item.Id)).Append(outcome).ToArray());
    await Finish(outcome);
}

async Task Finish(ResearchOutcome outcome)
{
    await status.Set(ResearchState.Display(outcome));
    await active.Set("");
}
