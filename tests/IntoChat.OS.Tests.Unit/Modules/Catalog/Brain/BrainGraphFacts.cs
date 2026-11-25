using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Brain;

public sealed class BrainGraphFacts
{
    private static readonly BrainTopology Topology = new(
        [new("ada/notes/default", "notes", "unit"), new("ada/apps.app2/apps/files", "files", "app"), new("ada/synapse.installs/default", "synapse.installs", "observed")],
        [new("*", "ada/notes/default", "note.added", "reaction"), new("*", "ada/notes/default", "note.removed", "gate"), new("*", "ada/gone/default", "x", "reaction")]);

    // The select the Creator passes: its node's key, or none for the scene's (the picked edge or pulse key travels as the value).
    private static Command Select(string? key) => new(".", "creator.select", key is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(new { key }));

    private static FlowView Build(string subject, string[] done, FlowStatus status = FlowStatus.Running) => new(
        "ada/flow.build/1", BuildFlow.Alias, "build", "ada", "t1", status, ["author", "draft", "verify", "deploy", "install"], done, "draft",
        null, null, null, 0.2, [], [], null, DateTimeOffset.UnixEpoch, null, null, Subject: subject);

    [Fact]
    public void UnitsAndTheirReactionsAreNodesAndEdges()
    {
        var scene = BrainGraph.Build(Topology, "ada/notes/default", Select);

        Assert.Equal(["unit", "app", "kernel"], scene.Nodes.Select(node => node.Kind));
        Assert.Equal(2, scene.Synapses.Count);
        Assert.All(scene.Synapses, edge => Assert.Equal(("*", "ada/notes/default"), (edge.FromId, edge.ToId)));
        Assert.Equal(["note.added", "note.removed#gate"], scene.Synapses.Select(edge => edge.SignalLabel));
        Assert.Equal("ada/notes/default", scene.SelectedId);
        Assert.Null(BrainGraph.Build(Topology, "nobody", Select).SelectedId);
    }

    [Fact]
    public void ADraftIsAGhostNode()
    {
        var scene = BrainGraph.Build(Topology with { Flows = [Build("weather", ["author"]), Build("notes", ["author"]), Build("early", []), Build("dead", ["author"], FlowStatus.Failed)] }, null, Select);

        var draft = Assert.Single(scene.Nodes, node => node.Kind == BrainGraph.Draft);
        Assert.Equal(("draft:weather", "weather", "building"), (draft.Id, draft.Label, draft.Status));
    }

    [Fact]
    public void AReactionAndAGateOnOneAliasStayTwoEdges()
    {
        var scene = BrainGraph.Build(Topology with { Edges = [new("*", "ada/notes/default", "note.added", "reaction"), new("*", "ada/notes/default", "note.added", "gate")] }, null, Select);

        Assert.Equal(["note.added", "note.added#gate"], scene.Synapses.Select(edge => edge.SignalLabel));
    }

    [Fact]
    public void AGhostIsSuppressedByTheUnitIdNotItsLabel()
    {
        var topology = new BrainTopology([new("ada/weather/default", "Weather station", "unit")], []) { Flows = [Build("weather", ["author"])] };

        Assert.DoesNotContain(BrainGraph.Build(topology, null, Select).Nodes, node => node.Kind == BrainGraph.Draft);
    }

    [Fact]
    public void KeysFollowTheInspectorContract()
    {
        var scene = BrainGraph.Build(Topology, null, Select);

        Assert.Equal("node:ada/notes/default", scene.Nodes[0].Select!.Key);
        Assert.Equal(Select("node:ada/notes/default"), scene.Nodes[0].Select!.Command);
        Assert.Equal(Select(null), scene.Select);
        Assert.Equal("edge:*→ada/notes/default:note.added", BrainGraph.EdgeKey("*", "ada/notes/default", "note.added"));
        Assert.Equal("pulse:t1", BrainGraph.PulseKey("t1"));
    }
}
