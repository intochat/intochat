using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Text;

// The assistant tool path: "send" by request. The composer path lives in submissions.cs.
// Jev first: the request is asked of the router and this reaction ends; routed.cs answers it.
// A replayed request is skipped, so Jev is asked once per message.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var router = brain.Get<IRouter>(Ino.Scope(brain));
var ledger = await RequestLedger.LoadAsync(brain, "send");
await foreach (var request in brain.On(app, "send", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    // Jev keeps the requests it was asked: one it already holds was sent before this ledger saw it.
    if (await router.Pending(request.RequestId, brain.Stopping) is not null) { await ledger.Record(request.RequestId); continue; }
    if (string.IsNullOrWhiteSpace(request.Input) || request.Input.Length > 32000) { await ledger.Record(request.RequestId); continue; }
    await Ino.Advance(brain, "listening", "Heard you");
    // A pasted workspace link is an app card, never a turn: neither Jev nor the model sees it.
    if (InoLinks.Parse(request.Input) is not null)
    {
        try
        {
            var outcome = await InoLinks.Handle(brain.Get<IAgent>((await Ino.EnsureSelection(brain)).Agent),
                brain.Get<IText>(Ino.DataKey(brain) + "/links"), brain.Get<IText>(Ino.DataKey(brain) + "/status"),
                (state, detail) => Ino.Advance(brain, state, detail), request.Input, request.RequestId);
            if (outcome == InoLinkOutcome.Busy) { await Ino.Status(brain, InoCopy.AnswerRunningResend); }
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Ino.Status(brain, Ino.Failure(InoCopy.LinkFailed, error)); }
        await ledger.Record(request.RequestId);
        continue;
    }
    await Ino.Touched(brain, (await Ino.EnsureSelection(brain)).Agent, request.Input);
    if (request.Input.Length > Ino.MaxRoutedChars)
    {
        await Ino.Status(brain, InoCopy.LongMessage);
        var snapshot = await app.Read();
        var conversation = (await Ino.EnsureSelection(brain)).Agent;
        if (!await Ino.Intend(brain, snapshot, request.Input, "", request.RequestId, conversation, "send"))
        { await Ino.Answer(brain, snapshot, request.Input, "", request.RequestId); }
    }
    else
    {
        try { await router.Ask(new RouteRequest(request.RequestId, request.Input), brain.Stopping); }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await Ino.Status(brain, Ino.Failure(InoCopy.RouterUnavailable, error));
            await Ino.Advance(brain, "attention", "Jev unavailable; asking the model");
            await Ino.Answer(brain, await app.Read(), request.Input, "", request.RequestId);
        }
    }
    await ledger.Record(request.RequestId);
}
