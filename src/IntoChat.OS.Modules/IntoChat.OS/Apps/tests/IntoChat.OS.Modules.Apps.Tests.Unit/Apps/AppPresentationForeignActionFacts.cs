using System.Text.Json;
using DigitalBrain.Confirmation;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPresentationForeignActionFacts
{
    private static readonly ApprovalCardNode Approval = new("approval", "plan-1", "Approve plan", "Detail", [], Resolve: new Resolve("plan-1"));

    [Fact]
    public void AnOsTrustedAppsApprovalCardIsAnswerableThroughTheConfirmationsInbox()
    {
        var mapped = PresentationCollaborator.MapNode("n", Approval, "owner", osTrusted: true);

        foreach (var (suffix, answer) in new[] { ("approve", "true"), ("reject", "false") })
        {
            var button = Assert.Single(mapped.Children!, child => child.Key == $"n.{suffix}");
            Assert.True(NeuronButtonAction.TryParse(button.Action, out var key, out var operation, out var arguments));
            Assert.Equal(IConfirmations.ConfirmationsNeuronKey, key);
            Assert.Equal(IConfirmations.ResolveOperation, operation);
            Assert.Equal(["plan-1", answer], arguments);
        }
    }

    [Fact]
    public void AModelAuthoredAppsApprovalCardIsInert()
    {
        foreach (var mapped in new[] { PresentationCollaborator.MapNode("n", Approval, "owner"), PresentationCollaborator.MapNode("n", Approval, "owner", osTrusted: false) })
        {
            var approve = Assert.Single(mapped.Children!, child => child.Key == "n.approve");
            Assert.False(NeuronButtonAction.TryParse(approve.Action, out _, out _, out _));
            Assert.Equal($"{IConfirmations.ResolveOperation}:plan-1:true", approve.Action);
        }
    }

    [Fact]
    public void AnOpenWindowIntentOpensTheWindowNamedByItsArgument()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("open", "Open trace", Intent: new Intent(IntentKind.OpenWindow, "os://window/open?app=trace&input=trace%2Ft-1")), "owner");
        Assert.Equal("os://window/open?app=trace&input=trace%2Ft-1", mapped.Action);
    }

    [Fact]
    public void ACopyIntentIsACopyAction()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("copy", "Copy", Intent: new Intent(IntentKind.Copy, "neuron://owner/x")), "owner");
        Assert.StartsWith("copy:", mapped.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmitStaysAnAppOperationWithItsArgumentsColonSeparated()
    {
        var one = PresentationCollaborator.MapNode("n", new Button("cancel", "Cancel", new Emit("tasks", "cancel", JsonSerializer.SerializeToElement("f/1"))), "owner");
        Assert.Equal("cancel:f%2F1", one.Action);
        var many = PresentationCollaborator.MapNode("n", new Button("pin", "Pin", new Emit("desktop", "pin", JsonSerializer.SerializeToElement(new[] { "markdraw", "true" }))), "owner");
        Assert.Equal("pin:markdraw:true", many.Action);
        var none = PresentationCollaborator.MapNode("n", new Button("refresh", "Refresh", new Emit("trace", "refresh")), "owner");
        Assert.Equal("refresh", none.Action);
    }

    [Fact]
    public void AFieldNamedByAKeyIsTheBrainsTextfield()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Field("title", "apps/tasks/title", FieldKind.Text, "Title"), "owner");
        Assert.Contains("\"name\":\"owner/apps/tasks/title\"", mapped.Detail, StringComparison.Ordinal);
        var plain = PresentationCollaborator.MapNode("n", new Field("query", "query", FieldKind.Search, "Search"), "owner");
        Assert.Contains("\"name\":\"query\"", plain.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmitArgumentWithColonsIsEscapedSoTheLegacyActionStringKeepsItWhole()
    {
        var mapped = PresentationCollaborator.MapNode("n", new Button("pulse", "Approve", new Emit("gallery", "select", JsonSerializer.SerializeToElement("card:approval:approved"))), "owner");

        Assert.Equal("select:card%3Aapproval%3Aapproved", mapped.Action);
    }
}
