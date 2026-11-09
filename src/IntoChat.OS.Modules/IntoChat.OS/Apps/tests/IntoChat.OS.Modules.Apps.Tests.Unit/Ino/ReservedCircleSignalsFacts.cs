using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Ino;

// The room trusts circleroom.join and .leave as the circle's: only the room's own code may name a circleroom signal.
public sealed class ReservedCircleSignalsFacts
{
    [Fact]
    public void AnotherUnitNamingACircleroomSignalIsRefused()
    {
        var forging = new Dictionary<string, string> { ["Forge.cs"] = "new Button(\"j\", \"Join\", new Emit(\"forge\", \"circleroom.join\", args))" };

        Assert.Equal("circleroom", SynapseStudioNeuron.ReservedSignal("forge", forging));
        Assert.Null(SynapseStudioNeuron.ReservedSignal("circleroom", forging));
        Assert.Null(SynapseStudioNeuron.ReservedSignal("x.watch", new Dictionary<string, string> { ["XWatch.cs"] = "[Signal(\"x.polled\")]" }));
    }
}
