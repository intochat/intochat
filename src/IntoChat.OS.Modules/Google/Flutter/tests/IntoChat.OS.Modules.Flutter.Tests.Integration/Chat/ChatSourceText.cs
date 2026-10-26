namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

// The fake agent a chat follows in these facts: a plain neuron (06-08), as the agent is, that the plain chat follows through a
// synapse.
[DigitalBrain.Contracts.NeuronAlias("test.chat-source"), DigitalBrain.Contracts.DefaultNeuronType(ChatSourceTextNeuron.Type)]
public interface IChatSourceText
{
    Task Set(string markdown);
    Task<ChatSourceTextState> Read();
}

public sealed class ChatSourceTextState
{
    public string Markdown { get; set; } = "";
    public int Version { get; set; }
}

[DigitalBrain.Signal("test.chat-source-changed")]
public sealed record ChatSourceTextChange(int Version) : DigitalBrain.Signal;

[DigitalBrain.Neuron(Type)]
public sealed class ChatSourceTextNeuron : DigitalBrain.Neuron<ChatSourceTextState>, IChatSourceText
{
    public const string Type = "test.chat-source";

    public Task Set(string markdown)
        => Save(new ChatSourceTextState { Markdown = markdown, Version = State.Version + 1 }, new ChatSourceTextChange(State.Version + 1));

    public Task<ChatSourceTextState> Read() => Task.FromResult(State);
}

// Hosts the fake agent for the TestBrain facts that follow it.
[DigitalBrain.Module("test.chat-source")]
public sealed class ChatSourceModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
