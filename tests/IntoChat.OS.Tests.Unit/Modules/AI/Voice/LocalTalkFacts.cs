#pragma warning disable MEAI001 // Unit tests for LocalTalk IRealtimeClient pipeline.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Voice;

public sealed class LocalTalkFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASpokenTurnComesBackAsTranscriptTextAndAudio()
    {
        var vad = new ScriptedVad();
        var hear = new StubHear(["hello"]);
        var chat = new HeldChat(["Hi there. ", "How are you?"], holdAfterFirst: false);
        var speak = new StubSpeak();
        using var talk = new LocalTalk(hear, chat, speak, vad);
        await using var session = await talk.CreateSessionAsync(cancellationToken: Ct);

        vad.EnqueueEnded([1, 2, 3, 4]);
        await SendChunkAsync(session);

        var output = await ReadUntilAsync(session, static m => m.Type == RealtimeServerMessageType.ResponseDone);

        var transcript = Assert.Single(output.OfType<InputAudioTranscriptionRealtimeServerMessage>());
        Assert.Equal("hello", transcript.Transcription);
        Assert.Equal(2, output.Count(static m => m.Type == RealtimeServerMessageType.OutputAudioDelta));
        Assert.Equal(
            "Hi there. How are you?",
            string.Concat(output.OfType<OutputTextAudioRealtimeServerMessage>()
                .Where(static m => m.Type == RealtimeServerMessageType.OutputTextDelta)
                .Select(static m => m.Text)));
        Assert.Equal(RealtimeServerMessageType.ResponseDone, output[^1].Type);
        Assert.Equal(["Hi there.", "How are you?"], speak.SpokenTexts);
    }

    [Fact]
    public async Task SpeechDuringPlaybackCancelsTheReply()
    {
        var vad = new ScriptedVad();
        var hear = new StubHear(["tell me a story"]);
        var chat = new HeldChat(["One. ", "Two. ", "Three."], holdAfterFirst: true);
        var speak = new StubSpeak();
        using var talk = new LocalTalk(hear, chat, speak, vad);
        await using var session = await talk.CreateSessionAsync(cancellationToken: Ct);

        vad.EnqueueEnded([1, 2, 3, 4]);
        await SendChunkAsync(session);

        // Wait until first sentence has been streamed by chat AND started/finished in speak.
        await chat.SentFirstSentence.WaitAsync(Ct);
        await speak.CalledOnce.WaitAsync(Ct);

        // User speaks for 300ms (>= 250ms default BargeIn).
        vad.EnqueueSpeaking(TimeSpan.FromMilliseconds(300));
        await SendChunkAsync(session);

        var output = await ReadUntilAsync(session, static m => m.Type == LocalTalk.Interrupted);
        await session.DisposeAsync();
        // Also drain anything remaining in the channel after dispose (must contain no audio delta).
        var tail = await DrainRemainingAsync(session);
        var all = output.Concat(tail).ToList();

        Assert.Contains(all, static m => m.Type == LocalTalk.Interrupted);
        Assert.True(chat.Token.IsCancellationRequested);
        Assert.True(speak.LastToken.IsCancellationRequested);
        Assert.DoesNotContain(
            all.SkipWhile(static m => m.Type != LocalTalk.Interrupted),
            static m => m.Type == RealtimeServerMessageType.OutputAudioDelta);
    }

    [Fact]
    public async Task ShortNoiseDoesNotBargeIn()
    {
        var vad = new ScriptedVad();
        var hear = new StubHear(["count"]);
        var chat = new HeldChat(["One. ", "Two. ", "Three."], holdAfterFirst: true);
        var speak = new StubSpeak();
        using var talk = new LocalTalk(hear, chat, speak, vad);
        await using var session = await talk.CreateSessionAsync(cancellationToken: Ct);

        vad.EnqueueEnded([1, 2, 3, 4]);
        await SendChunkAsync(session);

        await chat.SentFirstSentence.WaitAsync(Ct);
        await speak.CalledOnce.WaitAsync(Ct);

        // 100ms noise is below the 250ms barge-in threshold.
        vad.EnqueueSpeaking(TimeSpan.FromMilliseconds(100));
        await SendChunkAsync(session);

        chat.Release();
        var output = await ReadUntilAsync(session, static m => m.Type == RealtimeServerMessageType.ResponseDone);

        Assert.DoesNotContain(output, static m => m.Type == LocalTalk.Interrupted);
        Assert.Equal(3, output.Count(static m => m.Type == RealtimeServerMessageType.OutputAudioDelta));
        Assert.Equal(["One.", "Two.", "Three."], speak.SpokenTexts);
    }

    [Fact]
    public async Task AnEmptyTranscriptStartsNoTurn()
    {
        var vad = new ScriptedVad();
        var hear = new StubHear(["   ", "real question"]);
        var chat = new HeldChat(["Answer."], holdAfterFirst: false);
        var speak = new StubSpeak();
        using var talk = new LocalTalk(hear, chat, speak, vad);
        await using var session = await talk.CreateSessionAsync(cancellationToken: Ct);

        // First ended segment transcribes to whitespace (must start no turn); second runs normally.
        vad.EnqueueEnded([1, 2]);
        await SendChunkAsync(session);
        vad.EnqueueEnded([3, 4]);
        await SendChunkAsync(session);

        var output = await ReadUntilAsync(session, static m => m.Type == RealtimeServerMessageType.ResponseDone);

        Assert.Equal(1, chat.Calls);
        Assert.Equal("real question", chat.LastUserMessage);
        var transcript = Assert.Single(output.OfType<InputAudioTranscriptionRealtimeServerMessage>());
        Assert.Equal("real question", transcript.Transcription);
    }

    [Fact]
    public async Task AFailedHearReportsAndKeepsListening()
    {
        var vad = new ScriptedVad();
        var hear = new StubHear([new InvalidOperationException("decoder boom"), "recovered"]);
        var chat = new HeldChat(["Back online."], holdAfterFirst: false);
        var speak = new StubSpeak();
        using var talk = new LocalTalk(hear, chat, speak, vad);
        await using var session = await talk.CreateSessionAsync(cancellationToken: Ct);

        vad.EnqueueEnded([1, 2]);
        await SendChunkAsync(session);
        vad.EnqueueEnded([3, 4]);
        await SendChunkAsync(session);

        var output = await ReadUntilAsync(session, static m => m.Type == RealtimeServerMessageType.ResponseDone);

        var error = Assert.Single(output.OfType<ErrorRealtimeServerMessage>());
        Assert.Equal(RealtimeServerMessageType.Error, error.Type);
        Assert.Contains("decoder boom", error.Error?.Message ?? string.Empty, StringComparison.Ordinal);

        var transcript = Assert.Single(output.OfType<InputAudioTranscriptionRealtimeServerMessage>());
        Assert.Equal("recovered", transcript.Transcription);
        Assert.Equal(1, output.Count(static m => m.Type == RealtimeServerMessageType.OutputAudioDelta));
        Assert.Equal(RealtimeServerMessageType.ResponseDone, output[^1].Type);
    }

    [Fact]
    public void SentencesSplitOnTerminators()
    {
        var pending = new StringBuilder("Hi. Yes! And");
        var completed = SentenceChunker.Complete(pending).ToArray();
        Assert.Equal(["Hi.", "Yes!"], completed);
        Assert.Equal("And", pending.ToString());

        var withDecimal = new StringBuilder("Pi is 3.14 roughly. Next\nRest");
        Assert.Equal(["Pi is 3.14 roughly.", "Next"], SentenceChunker.Complete(withDecimal).ToArray());
        Assert.Equal("Rest", withDecimal.ToString());
    }

    private static Task SendChunkAsync(IRealtimeClientSession session)
        => session.SendAsync(
            new InputAudioBufferAppendRealtimeClientMessage(new DataContent(new byte[320], "audio/pcm;rate=16000")),
            Ct);

    private static async Task<List<RealtimeServerMessage>> ReadUntilAsync(
        IRealtimeClientSession session,
        Func<RealtimeServerMessage, bool> stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var messages = new List<RealtimeServerMessage>();
        await foreach (var message in session.GetStreamingResponseAsync(timeout.Token))
        {
            messages.Add(message);
            if (stop(message))
            {
                break;
            }
        }

        return messages;
    }

    private static async Task<List<RealtimeServerMessage>> DrainRemainingAsync(IRealtimeClientSession session)
    {
        var messages = new List<RealtimeServerMessage>();
        await foreach (var message in session.GetStreamingResponseAsync(Ct))
        {
            messages.Add(message);
        }

        return messages;
    }

    private sealed class ScriptedVad : IVoiceActivityDetector
    {
        private readonly ConcurrentQueue<VoiceActivity> _script = new();

        public bool Ready => true;

        public void EnqueueSpeaking(TimeSpan speakingFor)
            => _script.Enqueue(new VoiceActivity(Speaking: true, SpeakingFor: speakingFor, Ended: []));

        public void EnqueueEnded(byte[] segment)
            => _script.Enqueue(new VoiceActivity(Speaking: false, SpeakingFor: TimeSpan.Zero, Ended: [segment]));

        public IVoiceActivity Open(int sampleRate = 16_000) => new Session(_script);

        private sealed class Session(ConcurrentQueue<VoiceActivity> script) : IVoiceActivity
        {
            public VoiceActivity Push(ReadOnlySpan<byte> pcm16)
                => script.TryDequeue(out var next) ? next : new VoiceActivity(false, TimeSpan.Zero, []);

            public void Dispose() { }
        }
    }

    private sealed class StubHear(params object[] outcomes) : ISpeechToTextClient
    {
        private readonly ConcurrentQueue<object> _outcomes = new(outcomes);

        public Task<SpeechToTextResponse> GetTextAsync(
            Stream audioSpeechStream,
            SpeechToTextOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_outcomes.TryDequeue(out var next))
            {
                return Task.FromResult(new SpeechToTextResponse(string.Empty));
            }

            if (next is Exception ex)
            {
                return Task.FromException<SpeechToTextResponse>(ex);
            }

            return Task.FromResult(new SpeechToTextResponse((string)next));
        }

        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
            Stream audioSpeechStream,
            SpeechToTextOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var r = await GetTextAsync(audioSpeechStream, options, cancellationToken);
            yield return new SpeechToTextResponseUpdate(r.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HeldChat(string[] chunks, bool holdAfterFirst) : IChatClient
    {
        private readonly TaskCompletionSource _sentFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string? LastUserMessage { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task SentFirstSentence => _sentFirst.Task;

        public void Release() => _gate.TrySetResult();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastUserMessage = messages.LastOrDefault(static m => m.Role == ChatRole.User)?.Text;
            Token = cancellationToken;

            for (var i = 0; i < chunks.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunks[i]);
                if (i == 0)
                {
                    _sentFirst.TrySetResult();
                    if (holdAfterFirst)
                    {
                        await _gate.Task.WaitAsync(cancellationToken);
                    }
                }
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubSpeak : ITextToSpeechClient
    {
        private readonly TaskCompletionSource _calledOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _spoken = new();

        public Task CalledOnce => _calledOnce.Task;
        public CancellationToken LastToken { get; private set; }
        public IReadOnlyList<string> SpokenTexts => [.. _spoken];

        public Task<TextToSpeechResponse> GetAudioAsync(
            string text,
            TextToSpeechOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            _spoken.Enqueue(text);
            _calledOnce.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TextToSpeechResponse([new DataContent(new byte[] { 0x01, 0x02 }, "audio/pcm;rate=24000")]));
        }

        public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
            string text,
            TextToSpeechOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var r = await GetAudioAsync(text, options, cancellationToken);
            yield return new TextToSpeechResponseUpdate(r.Contents);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
