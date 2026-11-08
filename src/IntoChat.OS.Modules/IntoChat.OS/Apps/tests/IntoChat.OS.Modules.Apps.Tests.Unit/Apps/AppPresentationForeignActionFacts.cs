using DigitalBrain.Confirmation;
using DigitalBrain.UI;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPresentationForeignActionFacts
{
    [Fact]
    public void AnOsTrustedAppsConfirmationsButtonIsANeuronButton()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("Approve", new ButtonAction(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"])), "owner", osTrusted: true);

        Assert.True(NeuronButtonAction.TryParse(mapped.Action, out var key, out var operation, out var arguments));
        Assert.Equal(IConfirmations.ConfirmationsNeuronKey, key);
        Assert.Equal(IConfirmations.ResolveOperation, operation);
        Assert.Equal(["plan-1", "true"], arguments);
    }

    [Fact]
    public void AModelAuthoredAppsConfirmationsButtonIsInert()
    {
        var button = new Button("Dismiss", new ButtonAction(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"]));

        foreach (var mapped in new[] { PresentationCollaborator.MapNode("n", button, "owner"), PresentationCollaborator.MapNode("n", button, "owner", osTrusted: false) })
        {
            Assert.False(NeuronButtonAction.TryParse(mapped.Action, out _, out _, out _));
            Assert.Equal($"{IConfirmations.ResolveOperation}:plan-1:true", mapped.Action);
        }
    }

    [Fact]
    public void AnAppOpenWithAViewOpensTheWindowWithThatInput()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("Open trace", new ButtonAction("apps/trace", "open", ["trace/t-1"])), "owner");
        Assert.Equal("os://window/open?app=trace&input=trace%2Ft-1", mapped.Action);
        var plain = PresentationCollaborator.MapNode("n", new Button("Open", new ButtonAction("apps/trace", "open", [])), "owner");
        Assert.Equal("os://window/open?app=trace", plain.Action);
    }

    [Fact]
    public void AnOwnButtonStaysAnAppOperation()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("Cancel", new ButtonAction("tasks", "cancel", ["f/1"])), "owner");
        Assert.Equal("cancel:f%2F1", mapped.Action);
    }

    [Fact]
    public void AFieldNamedByAKeyIsTheBrainsTextfield()
    {
        var mapped = PresentationCollaborator.MapNode("n", new TextField("apps/tasks/title", "", "Title"), "owner");
        Assert.Contains("\"name\":\"owner/apps/tasks/title\"", mapped.Detail, StringComparison.Ordinal);
        var plain = PresentationCollaborator.MapNode("n", new TextField("query", "", "Search"), "owner");
        Assert.Contains("\"name\":\"query\"", plain.Detail, StringComparison.Ordinal);
    }
}
