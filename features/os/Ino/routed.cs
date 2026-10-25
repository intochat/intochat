using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.Routing.Signals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Workspace;

// Jev's answer to a request send.cs asked. A confident, direct route is carried out: opening the app
// the message names (the route Jev decided, never decided again), or a pure read through the router.
// Any other operation of a named app needs its input filled from the message, so the model answers with
// the route as a hint. A route back into the assistant, or to any operation of an app the message does
// not name, is not carried out: the model answers the message itself. The request lives in the router's
// state; the status line says which path answered.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var key = Ino.Key(brain);
var scope = Ino.Scope(brain);
var app = brain.Get<IApp>(key);
var router = brain.Get<IRouter>(scope);
var ledger = await RequestLedger.LoadAsync(brain, "routed");
await foreach (var routed in brain.On<Routed>(router, brain.Stopping))
{
    // A redelivered answer is skipped, so a route is carried out (and a status written) once per message.
    if (ledger.Handled(routed.Id)) { continue; }
    await Carry(routed);
    await ledger.Record(routed.Id);
}

async Task Carry(Routed routed)
{
    if (await router.Pending(routed.Id, brain.Stopping) is not { } asked) { return; }
    var snapshot = await app.Read();
    var hint = "";
    try
    {
        if (asked.Result is not { } route)
        {
            // Routed with no result means Jev answered and nothing fits (unsure, with no hint to give);
            // Failed means Jev itself could not answer.
            var answered = asked.Status == "Routed";
            await Ino.Status(brain, answered ? InoCopy.JevUnsure : InoCopy.RouterUnavailable);
        }
        else if (route.Confidence < Ino.RouteGate(snapshot) || !route.IsDirect)
        {
            await Ino.Status(brain, InoCopy.JevUnsureScored(route.Confidence));
            hint = "\n\n" + route.Hint();
        }
        else if (route.App is null)
        {
            var outcome = await router.Dispatch(asked.State, [route.Contract], brain.Stopping);
            var chosen = $"{route.Contract}.{route.Method}";
            await Ino.Status(brain, outcome.Dispatched
                ? InoCopy.RouteDone(chosen)
                : InoCopy.RouteNotCarried(chosen));
            if (outcome.Dispatched) { return; }
            hint = "\n\n" + route.Hint();
        }
        else if (route.App == "assistant" || IApp.Key(scope, route.App) == key)
        {
            await Ino.Status(brain, InoCopy.JevPointedBack(route.App, route.Operation, route.Confidence));
            hint = "\n\n" + route.Hint();
        }
        else
        {
            var chosen = $"{route.App}.{route.Operation}";
            var target = brain.Get<IApp>(IApp.Key(scope, route.App));
            var manifest = (await target.Read()).Manifest;
            var title = manifest.Title;
            if (!Names(asked.State, route.App, title))
            {
                // Jev guessed an app the person never named: the model answers the message itself, and the
                // guess is offered only as a weak hint, never as an operation whose input to fill.
                await Ino.Status(brain, InoCopy.JevSuggestedUnnamed(chosen, route.Confidence, title));
                hint = $"\n\nJev suggested the {title} app's \"{route.Operation}\", but the message does not name that app. "
                    + "Answer the message itself with your tools; use that app only if it clearly fits.";
            }
            else if (route.Operation != "open")
            {
                await Ino.Status(brain, InoCopy.JevSuggested(chosen, route.Confidence));
                hint = "\n\n" + route.Hint();
            }
            else
            {
                var workspace = brain.Get<IWorkspace>(scope);
                await workspace.Show(IApp.Key(scope, route.App), routed.Id.ToString(), (await workspace.Read()).Revision);
                await Ino.Status(brain, InoCopy.JevOpened(chosen, route.Confidence, title));
                return;
            }
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, Ino.Failure(InoCopy.RouterFailed, error)); }
    var conversation = (await Ino.EnsureSelection(brain)).Agent;
    if (!await Ino.Intend(brain, snapshot, asked.State, hint, routed.Id, conversation))
    { await Ino.Answer(brain, snapshot, asked.State, hint, routed.Id); }
}

// Jev is often confident about the wrong app; a message that names the app (by name or title) is not.
static bool Names(string message, string name, string title)
{
    static string Plain(string text) => text.Replace('-', ' ').Replace('_', ' ').Trim();
    return message.Contains(Plain(name), StringComparison.OrdinalIgnoreCase) || message.Contains(name, StringComparison.OrdinalIgnoreCase)
        || (Plain(title).Length > 0 && message.Contains(Plain(title), StringComparison.OrdinalIgnoreCase));
}
