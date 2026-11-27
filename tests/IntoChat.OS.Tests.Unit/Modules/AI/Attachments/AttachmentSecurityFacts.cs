#pragma warning disable MEAI001 // Unit tests for multimodal attachment security, UploadAsync redaction, and DownloadAsync headers.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Attachments;

public sealed class AttachmentSecurityFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] ValidPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] ValidWav = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20];
    private static readonly byte[] ValidPdf = "%PDF-1.7\n%test"u8.ToArray();
    private static readonly byte[] MzExecutable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
    private static readonly byte[] ElfExecutable = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];
    private static readonly byte[] MachOFatBinary = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x02];

    [Fact]
    public void ValidatorAcceptsValidImageVoiceNoteAndTextAttachments()
    {
        var img = AttachmentValidator.ValidateUpload("photo.png", "image/png", ValidPng);
        Assert.Equal(AttachmentValidator.KindImage, img.Kind);
        Assert.Equal("image/png", img.MediaType);
        Assert.Equal(ValidPng.Length, img.SizeBytes);

        var note = AttachmentValidator.ValidateUpload("clip.wav", "audio/wav", ValidWav, requireVoiceNote: true, durationMs: 4200);
        Assert.Equal(AttachmentValidator.KindVoiceNote, note.Kind);
        Assert.Equal("audio/wav", note.MediaType);
        Assert.Equal(4200, note.DurationMs);

        var pdf = AttachmentValidator.ValidateUpload("spec.pdf", "application/pdf", ValidPdf);
        Assert.Equal(AttachmentValidator.KindFile, pdf.Kind);

        var md = AttachmentValidator.ValidateUpload("notes.md", "text/markdown", Encoding.UTF8.GetBytes("# Hello\nWorld"));
        Assert.Equal(AttachmentValidator.KindFile, md.Kind);
    }

    [Fact]
    public void ValidatorRejectsMismatchedMagicBytesBomScriptsMachOAndHeaderInjection()
    {
        // PNG extension + MIME, but MZ executable or HTML/SVG payload.
        var ex1 = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("fake.png", "image/png", MzExecutable));
        Assert.Equal(415, ex1.StatusCode);

        var exHtml = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("spoof.wav", "audio/wav", "<html><script>alert(1)</script></html>"u8));
        Assert.Equal(415, exHtml.StatusCode);

        // ELF & fat Mach-O disguised as PDF.
        Assert.Equal(415, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("doc.pdf", "application/pdf", ElfExecutable)).StatusCode);
        Assert.Equal(415, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("doc.pdf", "application/pdf", MachOFatBinary)).StatusCode);

        // Mismatched extension vs MIME.
        Assert.Equal(415, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("photo.jpg", "image/png", ValidPng)).StatusCode);

        // Active HTML/SVG/JS scripts & UTF-8 BOM-prefixed active markup rejected.
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("xss.svg", "image/svg+xml", "<svg onload=alert(1)>"u8));
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("page.txt", "text/plain", "\uFEFF<script>alert(1)</script>"u8));
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("run.ps1", "text/plain", "Write-Host hi"u8));

        // Path traversal & header injection characters (", ;) in filename rejected.
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("../../etc/passwd.txt", "text/plain", "hello"u8));
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("bad\"name;evil.txt", "text/plain", "hello"u8));
    }

    [Fact]
    public void ValidatorEnforces10MbAudio25MbFileAndPerTurnCountCaps()
    {
        // Empty payload rejected.
        Assert.Equal(400, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("empty.png", "image/png", [])).StatusCode);

        // Audio > 10 MiB rejected with 413.
        var oversizedAudio = new byte[AttachmentValidator.MaxAudioAttachmentBytes + 1];
        ValidWav.CopyTo(oversizedAudio, 0);
        Assert.Equal(413, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("huge.wav", "audio/wav", oversizedAudio)).StatusCode);

        // General file > 25 MiB rejected with 413.
        var oversizedFile = new byte[AttachmentValidator.MaxFileAttachmentBytes + 1];
        ValidPng.CopyTo(oversizedFile, 0);
        Assert.Equal(413, Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("huge.png", "image/png", oversizedFile)).StatusCode);

        // Per-turn attachment count cap (> 6) rejected.
        var seven = Enumerable.Range(0, AttachmentValidator.MaxAttachmentsPerTurn + 1)
            .Select(i => new AttachmentRef(
                Guid.NewGuid().ToString("N"),
                AttachmentValidator.KindImage,
                $"img{i}.png",
                "image/png",
                1024,
                $"/ai/attachments/id{i}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => AttachmentValidator.ValidateTurnAttachments(seven));

        // Combined turn byte cap (> 50 MiB) rejected.
        var threeHuge = Enumerable.Range(0, 3)
            .Select(i => new AttachmentRef(
                Guid.NewGuid().ToString("N"),
                AttachmentValidator.KindImage,
                $"big{i}.png",
                "image/png",
                20 * 1024 * 1024,
                $"/ai/attachments/big{i}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => AttachmentValidator.ValidateTurnAttachments(threeHuge));
    }

    [Fact]
    public async Task AttachmentStoreEnforcesPerBrainAclAndPreventsIdorAndPathTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-att-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileAttachmentStore(tempDir);
            var brainA = new BrainId("alice");
            var brainB = new BrainId("bob");

            var validated = AttachmentValidator.ValidateUpload("secret.png", "image/png", ValidPng);
            var saved = await store.SaveAsync(brainA, ConversationId.Parse("main"), validated, ValidPng, null, Ct);

            // Owner brain can read and owns the attachment.
            Assert.True(await store.OwnsAsync(brainA, saved.Id, Ct));
            var readA = await store.TryReadAsync(brainA, saved.Id, Ct);
            Assert.NotNull(readA);
            Assert.Equal(ValidPng, readA!.Bytes);

            // Different brain CANNOT read or claim ownership (prevents IDOR).
            Assert.False(await store.OwnsAsync(brainB, saved.Id, Ct));
            Assert.Null(await store.TryReadAsync(brainB, saved.Id, Ct));
            Assert.Null(await store.TryReadImageDataUriAsync(brainB, saved.Id, Ct));

            // Path traversal IDs are rejected cleanly.
            Assert.False(await store.OwnsAsync(brainA, "../bob/" + saved.Id, Ct));
            Assert.Null(await store.TryReadAsync(brainA, "..\\..\\secret", Ct));
            Assert.False(AttachmentValidator.IsValidAttachmentId("../escape"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UploadEndpointRedactsVoiceNoteTranscriptFailsClosedWithoutRedactorAndHandlesSttErrors()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-att-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileAttachmentStore(tempDir);
            var caller = IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("alice"));
            var sttWithSecret = new StubVoiceClients(hearText: "My key is sk-super-secret-999 please save it.");
            var redactor = new StubRedactor("sk-super-secret-999");

            // 1. Happy path: UploadAsync transcribes voice note and redacts secret BEFORE storing.
            var http1 = CreateUploadContext(caller, ValidWav, "note.wav", "audio/wav", kind: "voice-note");
            var result1 = await AttachmentEndpoints.UploadAsync(http1, store, sttWithSecret, audioConverter: null, redactor, Ct);
            await result1.ExecuteAsync(http1);
            Assert.Equal(200, http1.Response.StatusCode);
            http1.Response.Body.Position = 0;
            var uploaded = await JsonSerializer.DeserializeAsync(http1.Response.Body, AttachmentJsonContext.Default.AttachmentRef, Ct);
            Assert.NotNull(uploaded);
            Assert.NotNull(uploaded!.Transcript);
            Assert.DoesNotContain("sk-super-secret-999", uploaded.Transcript!, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", uploaded.Transcript!, StringComparison.Ordinal);

            var persistedMeta = await store.TryReadMetadataAsync(caller.Brain, uploaded.Id, Ct);
            Assert.NotNull(persistedMeta);
            Assert.DoesNotContain("sk-super-secret-999", persistedMeta!.Transcript!, StringComparison.Ordinal);

            // 2. Fail closed when ISecretRedactor is null and STT returns a transcript -> 503.
            var http2 = CreateUploadContext(caller, ValidWav, "note2.wav", "audio/wav", kind: "voice-note");
            var result2 = await AttachmentEndpoints.UploadAsync(http2, store, sttWithSecret, audioConverter: null, redactor: null, Ct);
            await result2.ExecuteAsync(http2);
            Assert.Equal(503, http2.Response.StatusCode);

            // 3. Transient STT exception does NOT HTTP 500; persists clip cleanly with Transcript = null.
            var failingStt = new StubVoiceClients(hearError: new InvalidOperationException("STT provider timeout"));
            var http3 = CreateUploadContext(caller, ValidWav, "note3.wav", "audio/wav", kind: "voice-note");
            var result3 = await AttachmentEndpoints.UploadAsync(http3, store, failingStt, audioConverter: null, redactor, Ct);
            await result3.ExecuteAsync(http3);
            Assert.Equal(200, http3.Response.StatusCode);
            http3.Response.Body.Position = 0;
            var clipNoTranscript = await JsonSerializer.DeserializeAsync(http3.Response.Body, AttachmentJsonContext.Default.AttachmentRef, Ct);
            Assert.NotNull(clipNoTranscript);
            Assert.Null(clipNoTranscript!.Transcript);

            // 4. DownloadAsync handles non-ASCII filenames safely with RFC 5987 Content-Disposition and CSP sandbox.
            var pdfValidated = AttachmentValidator.ValidateUpload("résumé.pdf", "application/pdf", ValidPdf);
            var savedPdf = await store.SaveAsync(caller.Brain, null, pdfValidated, ValidPdf, null, Ct);
            var http4 = CreateDownloadContext(caller);
            var result4 = await AttachmentEndpoints.DownloadAsync(http4, savedPdf.Id, store, Ct);
            await result4.ExecuteAsync(http4);
            Assert.Equal(200, http4.Response.StatusCode);
            Assert.Equal("nosniff", http4.Response.Headers["X-Content-Type-Options"].ToString());
            Assert.Equal("default-src 'none'; sandbox", http4.Response.Headers["Content-Security-Policy"].ToString());
            var disposition = http4.Response.Headers.ContentDisposition.ToString();
            Assert.StartsWith("attachment;", disposition, StringComparison.Ordinal);
            Assert.Contains("filename*=utf-8''", disposition, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ModelContextProjectsVoiceNoteTranscriptsAndMultimodalAttachmentsAndThreadProjectionMirrorsThem()
    {
        var now = DateTimeOffset.UtcNow;
        var voiceNote = new AttachmentRef(
            Id: "0123456789abcdef0123456789abcdef",
            Kind: AttachmentValidator.KindVoiceNote,
            FileName: "note.wav",
            MediaType: "audio/wav",
            SizeBytes: 32_000,
            Url: "/ai/attachments/0123456789abcdef0123456789abcdef",
            DurationMs: 12_000,
            Transcript: "Please summarize this architecture diagram.");
        var image = new AttachmentRef(
            Id: "fedcba9876543210fedcba9876543210",
            Kind: AttachmentValidator.KindImage,
            FileName: "arch.png",
            MediaType: "image/png",
            SizeBytes: 16_000,
            Url: "/ai/attachments/fedcba9876543210fedcba9876543210");

        var said = new Said(
            EntryIds.Said("r1"),
            new Participant.Human(new AccountId("alice")),
            now,
            "Check these",
            Attachments: [voiceNote, image]);

        var content = ModelContext.ProjectSaidContent(
            said,
            a => a.Kind == AttachmentValidator.KindImage
                ? new AiImage("data:image/png;base64,iVBORw0KGgo=", a.MediaType)
                : null);

        Assert.Equal(2, content.Length);
        var text = Assert.IsType<AiText>(content[0]).Text;
        Assert.Contains("Check these", text, StringComparison.Ordinal);
        Assert.Contains("Voice note (0:12, note.wav)", text, StringComparison.Ordinal);
        Assert.Contains("Please summarize this architecture diagram.", text, StringComparison.Ordinal);
        Assert.Contains("Attached image: arch.png", text, StringComparison.Ordinal);
        var aiImage = Assert.IsType<AiImage>(content[1]);
        Assert.Equal("image/png", aiImage.MediaType);

        var view = new ConversationView(
            ConversationId.Parse("main"),
            1,
            [new Participant.Human(new AccountId("alice"))],
            [said],
            Active: null,
            Runs: [],
            Blocked: false,
            Contexts: []);
        var threadMessages = ConversationThreadProjection.ToThreadMessages(view);
        var userMsg = Assert.Single(threadMessages);
        Assert.Equal(2, userMsg.Attachments.Count);
        Assert.Equal("voice-note", userMsg.Attachments[0].Kind);
        Assert.Equal(12_000, userMsg.Attachments[0].DurationMs);
        Assert.Equal("Please summarize this architecture diagram.", userMsg.Attachments[0].Transcript);
        Assert.Equal("image", userMsg.Attachments[1].Kind);
    }

    private static DefaultHttpContext CreateUploadContext(Caller caller, byte[] body, string fileName, string contentType, string? kind = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ICallerAmbient>(new FixedCallerAmbient(caller))
            .AddLogging()
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = contentType;
        http.Request.QueryString = kind is null
            ? new QueryString($"?fileName={Uri.EscapeDataString(fileName)}")
            : new QueryString($"?fileName={Uri.EscapeDataString(fileName)}&kind={Uri.EscapeDataString(kind)}");
        http.Request.Body = new MemoryStream(body, writable: false);
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static DefaultHttpContext CreateDownloadContext(Caller caller)
    {
        var services = new ServiceCollection()
            .AddSingleton<ICallerAmbient>(new FixedCallerAmbient(caller))
            .AddLogging()
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class FixedCallerAmbient(Caller caller) : ICallerAmbient
    {
        public Caller? Current => caller;
    }

    private sealed class StubRedactor(string secret) : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text?.Replace(secret, "[REDACTED]", StringComparison.Ordinal));

        public ValueTask<int> TailLength(CancellationToken cancellationToken)
            => ValueTask.FromResult(secret.Length - 1);
    }

    private sealed class StubVoiceClients(string? hearText = null, Exception? hearError = null) : IVoiceClients
    {
        public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
            => Task.FromResult(new Voice<ISpeechToTextClient>(new StubStt(hearText, hearError), KnownModels.WhisperLargeV3Turbo.Id, null));

        public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
            => Task.FromResult(new Voice<ITextToSpeechClient>(null, null, "unavailable"));

        public Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
            => Task.FromResult(new TalkVoice(null, null, true, 16_000, "unavailable"));

        private sealed class StubStt(string? text, Exception? error) : ISpeechToTextClient
        {
            public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
                => error is not null ? Task.FromException<SpeechToTextResponse>(error) : Task.FromResult(new SpeechToTextResponse(text ?? string.Empty));

            public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
        }
    }
}
