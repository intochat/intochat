using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Runs;

namespace IntoChat.OS.Kernel.Tests.Unit.DevUI;

// The 3D brain shows every neuron and wire, and lights a neuron while a run it owns, or a run of the synapse it hosts, is still going.
public sealed class BrainPageFacts
{
    private static RunView Run(string owner, RunState state, string? synapse = null, string? waiting = null) =>
        new("r", RunKind.Workflow, "t", state, waiting, owner, null, DateTimeOffset.UnixEpoch, null, [], null, null, synapse);

    private static readonly DigitalBrain.Kernel.ObservedWire[] Wires =
        [new("owner/ino.files/ino/files", "owner/agent/x", "ino.files.write-requested"), new("owner/runs/runs/index", "owner/app.creator/creator", "runs.changed")];

    private static readonly BrainTopology Topology = new(
        [new("owner/ino.files/ino/files", "ino.files", "kernel"), new("owner/unit/pomodoro", "pomodoro", "unit"), new("owner/agent/x", "agent", "kernel")],
        [new("owner/ino.files/ino/files", "owner/agent/x", "ino.files.write-requested", "wire")]);

    [Fact]
    public void EveryNeuronAndWireIsDrawn()
    {
        var scene = BrainPage.Scene(Topology, Wires, []);
        Assert.Equal(5, scene.Nodes.Length);
        Assert.Contains(scene.Nodes, node => node.Id == "owner/runs/runs/index" && node.Role == "runs");
        Assert.Equal(("owner/ino.files/ino/files", "owner/agent/x"), (scene.Wires[0].From, scene.Wires[0].To));
        Assert.All(scene.Nodes, node => Assert.Null(node.Run));
    }

    [Fact]
    public void ALiveRunLightsItsOwnerAndItsSynapse()
    {
        var scene = BrainPage.Scene(Topology, Wires, [Run("ino.files/ino/files", RunState.Waiting, waiting: "approval cf-1"), Run("x", RunState.Running, synapse: "pomodoro")]);
        Assert.Equal("approval cf-1", scene.Nodes[0].Waiting);
        Assert.NotNull(scene.Nodes[1].Run);
    }

    [Fact]
    public void AFinishedRunLightsNothing()
        => Assert.All(BrainPage.Scene(Topology, Wires, [Run("ino.files/ino/files", RunState.Succeeded)]).Nodes, node => Assert.Null(node.Run));

    [Fact]
    public void ALiveRunNoNeuronHostsIsDrawnOnItsOwn()
    {
        var scene = BrainPage.Scene(Topology, Wires, [Run("approval-demo", RunState.Waiting, waiting: "approval cf-2") with { Id = "workflows/w1" }]);
        var run = Assert.Single(scene.Nodes, node => node.Id == "run:workflows/w1");
        Assert.Equal(("Workflow", "approval cf-2"), (run.Role, run.Waiting));
    }
}
