using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

// The fake agent a chat follows in these facts. It stays a legacy grain, as the agent does (06-05b): the plain chat follows it
// through the stand-in below, which admits the synapse, and hears its change as the plain twin of its signal.
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

// Admits plain subscribers of the legacy fake agent (R4-13); the grain itself publishes.
[DigitalBrain.Neuron("test.chat-source")]
public sealed class ChatSourceTextSource : DigitalBrain.Neuron;

// ChatSourceTextChanged as a plain subscriber decodes it from the shared BrainLog.
[DigitalBrain.Signal("test.chat-source-changed")]
public sealed record ChatSourceTextChange(int Version) : DigitalBrain.Signal;

// Hosts the stand-in and registers the twin for the TestBrain facts that follow the fake agent.
[DigitalBrain.Module("test.chat-source")]
public sealed class ChatSourceModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
