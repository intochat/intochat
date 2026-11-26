using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class SecurityInterruptsFacts
{
    private static ContextEntry Interrupt(string text)
        => new("n", text, [], [ContextTags.Interrupt], new EntryProvenance("w", DateTimeOffset.UtcNow, ContextTrust.User));

    [Fact]
    public void TheReasonIsTheFirstInterruptByContextThenName()
    {
        var interrupts = new SecurityInterrupts();
        interrupts.Keep("b/context", "a", Interrupt("b-a"));
        interrupts.Keep("a/context", "z", Interrupt("a-z"));
        interrupts.Keep("a/context", "m", Interrupt("a-m"));

        Assert.Equal("a-m", interrupts.Reason);

        interrupts.Remove("a/context", "m");
        Assert.Equal("a-z", interrupts.Reason);
    }
}
