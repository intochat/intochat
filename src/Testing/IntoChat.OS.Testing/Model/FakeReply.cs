namespace IntoChat.OS.Testing.Model;

public abstract record FakeReply
{
    public static FakeReply Text(string content) => new TextReply(content);
    public static FakeReply Tool(string name, string argumentsJson) => new ToolReply(name, argumentsJson);
    // The reply is sent only after the delay, so a caller's own timeout or gate can be exercised.
    public static FakeReply Delayed(TimeSpan delay, FakeReply reply) => new DelayedReply(delay, reply);
    public static implicit operator FakeReply(string content) { return Text(content); }

    internal sealed record TextReply(string Content) : FakeReply;
    internal sealed record ToolReply(string Name, string ArgumentsJson) : FakeReply;
    internal sealed record DelayedReply(TimeSpan Delay, FakeReply Reply) : FakeReply;
}
