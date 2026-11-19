using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Modules.Apps.Synapses;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Behavior is listed by form from where it lives, with the runs it made; Creator shows it and can start a workflow.
public sealed class SynapseDefinitionsFacts
{
    private static RunView Run(string id, RunKind kind, string synapse, RunState state, int minute) =>
        new(id, kind, "", state, null, "", null, DateTimeOffset.UnixEpoch.AddMinutes(minute), null, [], null, null, synapse);

    [Fact]
    public void EachFormIsListedWithItsRuns()
    {
        var runs = new[]
        {
            Run("w1", RunKind.Workflow, "approval-demo", RunState.Failed, 1),
            Run("w2", RunKind.Workflow, "approval-demo", RunState.Succeeded, 2),
            Run("f1", RunKind.Flow, "flow.plan", RunState.Waiting, 3),
            Run("t1", RunKind.AgentTurn, "ino", RunState.Succeeded, 4),
        };

        var all = SynapseDefinitions.Of(["pomodoro"], [("approval-demo", "waits for a person")], runs);

        Assert.Equal(["reaction:pomodoro", "workflow:approval-demo", "flow:flow.plan", "agent:ino"], all.Select(synapse => synapse.Id));
        var workflow = all.Single(synapse => synapse.Form == SynapseForm.Workflow);
        Assert.Equal((2, "Succeeded"), (workflow.Runs, workflow.LastRun));
        Assert.Equal(0, all[0].Runs);
        Assert.Null(all[0].LastRun);
    }

    // An installed sandbox app is behavior before it ever runs: it is listed with what it answers and when it ticks.
    [Fact]
    public void ADeployedAppIsASynapseEvenBeforeItRuns()
    {
        DeployedApp[] apps = [new("ticker", ["ping"], ["1m"]), new("quiet", [], [])];
        var all = SynapseDefinitions.Of([], [], [Run("c1", RunKind.App, "ticker", RunState.Succeeded, 1), Run("c2", RunKind.App, "gone", RunState.Failed, 2)], apps);

        var ticker = all.Single(synapse => synapse.Id == "app:ticker");
        Assert.Equal((SynapseForm.App, 1, "Succeeded"), (ticker.Form, ticker.Runs, ticker.LastRun));
        Assert.Contains("commands: ping", ticker.Description, StringComparison.Ordinal);
        Assert.Contains("every 1m", ticker.Description, StringComparison.Ordinal);
        Assert.Equal(0, all.Single(synapse => synapse.Id == "app:quiet").Runs);
        // An app that ran once but is no longer deployed is still known by its runs.
        Assert.Contains(all, synapse => synapse.Id == "app:gone");
    }

    [Fact]
    public void AnAgentTurnBelongsToItsApp() => Assert.Equal("ino", RunsState.AgentApp("owner/ino/conversations/main"));

    [Fact]
    public void CreatorOffersToRunAWorkflow()
    {
        SynapseDefinition[] synapses = [new("workflow:approval-demo", SynapseForm.Workflow, "approval-demo", "", 0, null)];
        var left = Assert.IsType<Column>(CreatorView.LeftPane(new CreatorState(), new BrainTopology([], []), [], [], synapses));

        Assert.Contains(left.Children.OfType<Heading>(), heading => heading.Value == "Synapses");
        var run = left.Children.OfType<Row>().SelectMany(row => row.Children).OfType<Button>().Single(button => button.Key == "synapse:workflow:approval-demo");
        Assert.Equal("start-workflow", run.Command!.Signal);
    }
}
