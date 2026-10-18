using IntoChat.OS.AI.Media;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// A captured recording is transcribed; the composer's state when recognition returns decides the outcome.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = Ino.DataKey(brain);
var composer = brain.Get<IComposer>(appKey + "/composer");
await foreach (var captured in brain.On<ComposerVoiceCaptured>(composer, () => Transcribe(brain.Stopping), brain.Stopping))
{
    await Transcribe(brain.Stopping);
}

async Task Transcribe(CancellationToken ct)
{
    if ((await composer.Read()).Voice is not { Status: "transcribing" } voice) { return; }
    var audio = await composer.ReadAudio(voice.Id);
    if (audio is null) { return; }
    try
    {
        var result = await brain.Get<ISpeechRecognizer>(appKey + "/recognizer")
            .Recognize(new SpeechRecognitionRequest(new MediaPayload(audio.Bytes, audio.MimeType), "voice.wav"), ct);
        // The person may have cleared or re-recorded meanwhile: the composer's state decides.
        var current = (await composer.Read()).Voice;
        if (current?.Id == voice.Id && current.Status == "transcribing") { await composer.CompleteVoice(voice.Id, result.Text); }
        else { await composer.CancelVoice(voice.Id); }
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    { await composer.CompleteVoice(voice.Id, null, "Transcription failed. Your draft is unchanged. Try recording again or type your message."); }
}
