using IntoChat.OS.Modules.Apps;

// "ask": the request is recorded under its request id, classified with one model call and handed to its
// builder; nothing here waits for a builder. A synapse goes to the fast path of its target app (follow.cs
// reads the outcome), an app is queued for the Creator (build.cs), a module is recorded as blocked.
// Delivery is at-least-once: a request already past classification is not handled again, and a request
// left Writing asks the fast path again under the same operation id, which the app answers once.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Engineer.Key(brain));
var scope = Engineer.Scope(brain);

await foreach (var asked in brain.On(app, "ask", brain.Stopping))
{
    var id = asked.RequestId.ToString();
    var request = await Engineer.Read(brain, id);
    if (request is { Status: not (Engineer.Statuses.Classifying or Engineer.Statuses.Writing) }) { continue; }
    try
    {
        if (request is null)
        {
            var received = DateTimeOffset.UtcNow;
            try
            {
                var (text, target, focus, note) = Engineer.Parse(asked.Input, scope);
                request = new Engineer.Request { Id = id, Text = text, App = target, Focus = focus, Note = note, Status = Engineer.Statuses.Classifying, ReceivedAt = received };
            }
            catch (ArgumentException refused)
            {
                // A refusal calls no model and builds nothing; it is still a request the list shows. The id is
                // listed first, so the change that follows the save renders it.
                await Engineer.Add(Engineer.Index(brain), id);
                await Engineer.Save(brain, new Engineer.Request
                {
                    Id = id, Text = Engineer.Clip(asked.Input.Trim(), 300), Status = Engineer.Statuses.Refused,
                    Outcome = "Request refused: " + refused.Message, ReceivedAt = received,
                });
                continue;
            }
            await Engineer.Add(Engineer.Index(brain), id);
            await Engineer.Save(brain, request);
        }
        if (request.Status == Engineer.Statuses.Classifying) { request = await Route(request); }
        if (request.Status == Engineer.Statuses.Writing)
        {
            // Records the request on the target app and returns at once; the outcome lands in its synapse list.
            await brain.Get<IApp>(IApp.Key(scope, request.Target)).WriteSynapse(request.Text, request.Focus, asked.RequestId);
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested && request is not null)
    { await Engineer.Save(brain, request with { Status = Engineer.Statuses.Failed, Outcome = Engineer.Clip(error.Message, 600) }); }
}

// Classifies the request and records where it goes; a synapse's target app exists before it is recorded Writing.
async Task<Engineer.Request> Route(Engineer.Request request)
{
    var classified = await Engineer.Classify(brain, await app.Read(), request.Text, brain.Stopping);
    request = request with { Kind = classified.Kind, Build = classified.Build, Reason = classified.Reason, ClassifyMs = classified.ElapsedMs };
    switch (classified.Kind)
    {
        case Engineer.Kinds.Synapse:
            var target = await Target(request.App, classified.App);
            if (target is null)
            {
                request = request with { Status = Engineer.Statuses.Refused, Outcome = $"Request refused: there is no installed app named \"{request.App}\" to write the synapse into." };
                break;
            }
            // The follower watches every target app; the target joins before the fast path can answer.
            await Engineer.Add(Engineer.Targets(brain), target, keep: 64);
            request = request with { Target = target, Status = Engineer.Statuses.Writing, Outcome = "The fast path is writing the synapse." };
            break;
        case Engineer.Kinds.App:
            request = request with { Status = Engineer.Statuses.Queued, Outcome = "Queued for the Creator: " + Engineer.Clip(classified.Build.Length > 0 ? classified.Build : request.Text, 200) };
            break;
        default:
            request = request with
            {
                Status = Engineer.Statuses.Blocked,
                Outcome = "Blocked: this needs a new module (" + Engineer.Clip(classified.Reason, 200) + "). Module builds are gated by NG-1..NG-5 "
                    + "and the Module Builder is not installed, so nothing was built; the request stays here as a wish.",
            };
            break;
    }
    await Engineer.Save(brain, request);
    if (request.Status == Engineer.Statuses.Queued) { await Engineer.Builds(brain).Set(request.Id); }
    return request;
}

// The app the person named wins; then the app the classifier named, when it is installed; then scratch.
// The Engineer never writes into itself.
async Task<string?> Target(string? named, string? classified)
{
    var own = Engineer.Name(brain);
    if (named is not null)
    {
        if (named == Engineer.Scratch) { await Engineer.EnsureScratch(brain); return named; }
        return named != own && (await brain.Get<IApp>(IApp.Key(scope, named)).Read()).Manifest.Title.Length > 0 ? named : null;
    }
    if (classified is not null && classified != own && classified != Engineer.Scratch
        && (await brain.Get<IApp>(IApp.Key(scope, classified)).Read()).Manifest.Title.Length > 0)
    { return classified; }
    await Engineer.EnsureScratch(brain);
    return Engineer.Scratch;
}
