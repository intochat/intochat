using IntoChat.OS.Framework.Platform.Connections;

namespace IntoChat.OS.Google.Gmail;

[GenerateSerializer, Alias("db.gmail.state")]
internal sealed class GmailState : ConnectionState
{
    [Id(8)] public bool CanCompose { get; set; }
    [Id(9)] public GmailDraftPreview? PendingDraft { get; set; }
    [Id(10)] public GmailDraftPreview? SubmittingDraft { get; set; }
}
