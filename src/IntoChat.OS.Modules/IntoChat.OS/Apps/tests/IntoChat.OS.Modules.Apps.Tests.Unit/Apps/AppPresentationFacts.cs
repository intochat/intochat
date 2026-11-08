using IntoChat.OS.Modules.Flutter;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPresentationFacts
{
    [Fact]
    public void PublishAndWithBlackboardProducePureStateTransitions()
    {
        var state = new AppState { BrainScopeId = "owner", ProgramRevision = 2 };
        var spec = new IntoChat.OS.Modules.Apps.Presentation(
            new Sphere("icon.svg", ["count"]),
            Views: [new PresentationView("main", new UiChildRef("view", "owner/apps/notes/view"))]);

        var published = new PresentationCollaborator(state).Publish(spec);
        Assert.NotNull(published);
        Assert.Null(state.Presentation);
        Assert.Equal(1, published!.Presentation!.Revision);

        // Idempotent publish returns null when unchanged
        Assert.Null(new PresentationCollaborator(published).Publish(spec));

        var updated = new PresentationCollaborator(published).WithBlackboard(new Dictionary<string, string> { ["count"] = "5" });
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Presentation!.Revision);
        Assert.Equal("5", updated.Presentation.Blackboard["count"]);
        Assert.Empty(published.Presentation.Blackboard);
    }
}
