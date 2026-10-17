using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text.Signals;

// App builds, one at a time: whenever "ask" queues an app request, every queued one goes to the Creator (the
// brain's app studio authors the spec and builds the behaviors) and records the app it made. A request
// found Building when this runs was interrupted by a restart of this script: it is recorded failed rather
// than built twice, and asking again builds it.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var scope = Engineer.Scope(brain);

await brain.React<TextChanged>(Engineer.Builds(brain), async ct =>
{
    var pending = (await Engineer.ReadAll(brain))
        .Where(request => request.Kind == Engineer.Kinds.App && request.Status is Engineer.Statuses.Queued or Engineer.Statuses.Building)
        .OrderBy(request => request.ReceivedAt).ToArray();
    foreach (var request in pending)
    {
        if (request.Status == Engineer.Statuses.Building)
        {
            await Engineer.Save(brain, request with { Status = Engineer.Statuses.Failed, Outcome = "The build was interrupted before it finished; ask again to build it." });
            continue;
        }
        var building = request with { Status = Engineer.Statuses.Building, Outcome = "The Creator is building the app." };
        await Engineer.Save(brain, building);
        try
        {
            var built = await brain.Get<IAppStudio>(scope).Build(request.Build.Length > 0 ? request.Build : request.Text, ct);
            await Engineer.Save(brain, building with
            {
                Target = built.Name,
                Status = built.Status == AppStatus.Live ? Engineer.Statuses.Built : Engineer.Statuses.Failed,
                Outcome = built.Status == AppStatus.Live ? $"The app {built.Name} is live (version {built.Version})."
                    : $"The Creator could not build {built.Name}: {Engineer.Clip(string.Join(" ", built.Activity.TakeLast(2)), 400)}",
            });
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { await Engineer.Save(brain, building with { Status = Engineer.Statuses.Failed, Outcome = "The Creator failed: " + Engineer.Clip(error.Message, 600) }); }
    }
}, brain.Stopping);
