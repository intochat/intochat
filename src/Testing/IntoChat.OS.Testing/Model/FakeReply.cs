namespace IntoChat.OS.Testing.Model;

public abstract record FakeReply
{
    public static FakeReply Text(string content) => new TextReply(content);
    public static FakeReply Tool(string name, string argumentsJson) => new ToolReply(name, argumentsJson);
    public static implicit operator FakeReply(string content) { return Text(content); }

    internal sealed record TextReply(string Content) : FakeReply;
    internal sealed record ToolReply(string Name, string ArgumentsJson) : FakeReply;
}
