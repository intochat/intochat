using DigitalBrain;

namespace IntoChat.OS.Apps.Ino;

[Signal("ino.open")] public sealed record InoOpen(string? Mode = null) : Signal;
[Signal("ino.retry")] public sealed record InoRetry : Signal;
[Signal("ino.use-local")] public sealed record InoUseLocalModel(string? Model = null, string? Value = null) : Signal;
[Signal("ino.choose-model")] public sealed record InoChooseModel(string? Model = null, bool MakeDefault = false) : Signal;
[Signal("ino.save-provider")] public sealed record InoSaveProvider(string? Value = null, string? Provider = null) : Signal;
[Signal("ino.link")] public sealed record InoPasteLink(Guid Request = default, string? App = null, string? Input = null, string? Text = null) : Signal;
[Signal("ino.new")] public sealed record InoNew : Signal;
[Signal("ino.new-conversation")] public sealed record InoNewConversation : Signal;
[Signal("ino.switch-conversation")] public sealed record InoSwitchConversation(string? ConversationId = null) : Signal;
[Signal("ino.chats")] public sealed record InoShowChats : Signal;
[Signal("ino.toggle-devui")] public sealed record InoToggleDevUi : Signal;
[Signal("ino.make")] public sealed record InoMake(string? Sentence = null, string[]? Focus = null) : Signal;
[Signal("ino.stop")] public sealed record InoStop : Signal;
[Signal("ino.dismiss-draft")] public sealed record InoDismissDraft : Signal;
[Signal("ino.smart")] public sealed record InoSmart(bool On = true) : Signal;
[Signal("ino.connect-db")] public sealed record InoConnectDb(string? ConnectionString = null, string? Alias = null) : Signal;
[Signal("ino.query-db")] public sealed record InoQueryDb(string? Sql = null) : Signal;
[Signal("ino.open-drawer")] public sealed record InoOpenDrawer(string? ArtifactId = null, string? FlowId = null, string? Title = null, string? LiveViewPath = null, string? Kind = null) : Signal;
[Signal("ino.close-drawer")] public sealed record InoCloseDrawer : Signal;
[Signal("ino.drawer-tab")] public sealed record InoSelectDrawerTab(string? Tab = null) : Signal;
[Signal("ino.pin-artifact")] public sealed record InoPinArtifact(string? FlowId = null, string? Alias = null, string? Title = null) : Signal;
[Signal("ino.clear-context")] public sealed record InoClearContext : Signal;

[Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null, string? Value = null) : Signal
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : !string.IsNullOrWhiteSpace(Text) ? Text : Value;
}
