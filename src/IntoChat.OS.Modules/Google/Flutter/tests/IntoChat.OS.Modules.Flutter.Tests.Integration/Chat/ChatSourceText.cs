using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

// The fake agent a chat follows in these facts. It stays a legacy grain while ChatBinding and ComposerNeuron (06-05) watch their
// source through the legacy path: a legacy watcher cannot hear a plain neuron (R4-14), and Text is plain since 06-04.
[Alias("test.chat-source"), Orleans.Metadata.DefaultGrainType("test.chat-source")]
public interface IChatSourceText : INeuron
{
    Task Set(string markdown);
    Task<ChatSourceTextState> Read();
}

[GenerateSerializer, Alias("test.chat-source-state")]
public sealed class ChatSourceTextState
{
    [Id(0)] public string Markdown { get; set; } = "";
    [Id(1)] public int Version { get; set; }
}

[GenerateSerializer, Alias("test.chat-source-changed")]
public sealed record ChatSourceTextChanged([property: Id(0)] int Version) : Signal;

[GrainType("test.chat-source")]
public sealed class ChatSourceTextNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ChatSourceTextState> store)
    : Neuron<ChatSourceTextState>(store), IChatSourceText
{
    public Task Set(string markdown)
    {
        var next = Snapshot;
        next.Markdown = markdown;
        next.Version++;
        return Save(next, new ChatSourceTextChanged(next.Version));
    }

    public Task<ChatSourceTextState> Read() => Task.FromResult(Snapshot);
}
