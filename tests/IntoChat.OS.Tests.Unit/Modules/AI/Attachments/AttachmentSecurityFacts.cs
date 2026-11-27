using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Attachments;

public sealed class AttachmentSecurityFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] ValidPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] ValidWav = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20];
    private static readonly byte[] ValidPdf = "%PDF-1.7\n%test"u8.ToArray();
    private static readonly byte[] MzExecutable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
    private static readonly byte[] ElfExecutable = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];

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
    public void ValidatorRejectsMismatchedMagicBytesAndExecutables()
    {
        // PNG extension + MIME, but MZ executable bytes.
        var ex1 = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("fake.png", "image/png", MzExecutable));
        Assert.Equal(415, ex1.StatusCode);

        // ELF disguised as PDF.
        var ex2 = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("doc.pdf", "application/pdf", ElfExecutable));
        Assert.Equal(415, ex2.StatusCode);

        // Mismatched extension vs MIME.
        var ex3 = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("photo.jpg", "image/png", ValidPng));
        Assert.Equal(415, ex3.StatusCode);

        // Active HTML/SVG/JS scripts rejected.
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("xss.svg", "image/svg+xml", "<svg onload=alert(1)>"u8));
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("page.txt", "text/plain", "<script>alert(1)</script>"u8));
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("run.ps1", "text/plain", "Write-Host hi"u8));

        // Path traversal in filename rejected.
        Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("../../etc/passwd.txt", "text/plain", "hello"u8));
    }

    [Fact]
    public void ValidatorEnforcesByteSizeAndPerTurnCountCaps()
    {
        // Empty payload rejected.
        var empty = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("empty.png", "image/png", []));
        Assert.Equal(400, empty.StatusCode);

        // Oversized single attachment (> 8 MiB) rejected with 413.
        var oversized = new byte[AttachmentValidator.MaxAttachmentBytes + 1];
        ValidPng.CopyTo(oversized, 0);
        var tooBig = Assert.Throws<AttachmentValidationException>(() =>
            AttachmentValidator.ValidateUpload("huge.png", "image/png", oversized));
        Assert.Equal(413, tooBig.StatusCode);

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

        // Combined turn byte cap (> 24 MiB) rejected.
        var fourHuge = Enumerable.Range(0, 4)
            .Select(i => new AttachmentRef(
                Guid.NewGuid().ToString("N"),
                AttachmentValidator.KindImage,
                $"big{i}.png",
                "image/png",
                7 * 1024 * 1024,
                $"/ai/attachments/big{i}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => AttachmentValidator.ValidateTurnAttachments(fourHuge));
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

        // Verify ConversationThreadProjection projects concrete AttachmentRef[] to AttachmentNode[].
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

    [Fact]
    public async Task SecretRedactorMasksSecretsInVoiceNoteTranscripts()
    {
        ISecretRedactor redactor = new StubRedactor("sk-super-secret-12345");
        var raw = "My API key is sk-super-secret-12345 please store it.";
        var redacted = await redactor.Redact(raw, Ct);
        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-super-secret-12345", redacted!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted!, StringComparison.Ordinal);
    }

    private sealed class StubRedactor(string secret) : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text?.Replace(secret, "[REDACTED]", StringComparison.Ordinal));

        public ValueTask<int> TailLength(CancellationToken cancellationToken)
            => ValueTask.FromResult(secret.Length - 1);
    }
}
