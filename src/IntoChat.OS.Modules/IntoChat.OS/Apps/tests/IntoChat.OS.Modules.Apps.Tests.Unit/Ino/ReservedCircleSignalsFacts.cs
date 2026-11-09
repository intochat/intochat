using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Ino;

// The room trusts circleroom.join and .leave as the circle's: only the room's own code may name them. A message is any unit's to
// send: its author is the "by" the host stamps, not one the sender names.
public sealed class ReservedCircleSignalsFacts
{
    [Fact]
    public void AnotherUnitNamingACircleroomSignalIsRefused()
    {
        var forging = new Dictionary<string, string> { ["Forge.cs"] = "new Button(\"j\", \"Join\", new Emit(\"forge\", \"circleroom.join\", args))" };

        Assert.Equal("circleroom", SynapseStudioNeuron.ReservedSignal("forge", forging));
        Assert.Null(SynapseStudioNeuron.ReservedSignal("circleroom", forging));
        Assert.Equal("circleroom", SynapseStudioNeuron.ReservedSignal("forge", new Dictionary<string, string> { ["Forge.cs"] = "[Synapse(\"circleroom.leave\", \"forge\")]" }));
        Assert.Equal("circleroom", SynapseStudioNeuron.ReservedSignal("forge", new Dictionary<string, string> { ["Forge.cs"] = "new Emit(\"circleroom\", \"circleroom.message\", args)" }));
        Assert.Null(SynapseStudioNeuron.ReservedSignal("x.watch", new Dictionary<string, string> { ["XWatch.cs"] = "[Signal(\"x.polled\")]" }));
    }
}
