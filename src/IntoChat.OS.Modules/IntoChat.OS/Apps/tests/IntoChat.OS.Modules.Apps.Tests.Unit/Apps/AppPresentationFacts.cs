using IntoChat.OS.Modules.Flutter;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPresentationFacts
{
    [Fact]
    public void PublishProducesPureStateTransitions()
    {
        var state = new AppState { BrainScopeId = "owner", ProgramRevision = 2 };
        var spec = new IntoChat.OS.Modules.Apps.Presentation(
            new Sphere("icon.svg", ["count"]),
            Views: [new PresentationView("main", new ViewRef("view", "owner/apps/notes/view"))]);

        var published = new PresentationCollaborator(state).Publish(spec);
        Assert.NotNull(published);
        Assert.Null(state.Presentation);
        Assert.Equal(1, published!.Presentation!.Revision);

        // Idempotent publish returns null when unchanged
        Assert.Null(new PresentationCollaborator(published).Publish(spec));
    }
}
