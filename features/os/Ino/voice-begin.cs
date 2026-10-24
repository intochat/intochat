using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Chat;

// The dock's mic: "voice" composes the surface and begins a voice message on the composer under the
// request id. The composer captures the recording and voice.cs transcribes it into the draft. A
// recording already being transcribed is never replaced; a capture that only began is begun again.
// A replayed request is skipped, so a finished capture is never begun again.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var key = Ino.Key(brain);
var app = brain.Get<IApp>(key);
var composer = brain.Get<IComposer>(Ino.DataKey(brain) + "/composer");
var ledger = await RequestLedger.LoadAsync(brain, "voice");
await foreach (var request in brain.On(app, "voice", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try
    {
        await Ino.ComposeAsync(brain);
        var state = await composer.Read();
        if (state.Voice is not { Status: "transcribing" })
        {
            await composer.BeginVoice(request.RequestId, state.Context, state.Generation);
            await Ino.Advance(brain, "listening", "Listening");
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, Ino.Failure(InoCopy.VoiceFailed, error)); }
    await ledger.Record(request.RequestId);
}
