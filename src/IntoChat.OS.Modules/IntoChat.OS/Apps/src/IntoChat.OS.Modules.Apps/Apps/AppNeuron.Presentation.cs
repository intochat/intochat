using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation)
    {
        await RequireExecutingAssignment("Present", expectedProgramRevision);
        ArgumentNullException.ThrowIfNull(presentation);
        if (presentation.Sphere is { } sphere) { ArgumentNullException.ThrowIfNull(sphere.BlackboardKeys); }
        var views = presentation.Views ?? [];
        var invalidView = views.Any(view => view is null || string.IsNullOrWhiteSpace(view.Id)
            || view.Id.Length > 64 || view.Root is null) || views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Length;
        var viewCount = presentation.Sphere is null && views.Length == 0
            ? 0
            : invalidView || views.Length > 16
                ? 17
                : Math.Max(1, views.Length);
        var declaredKeys = presentation.Sphere?.BlackboardKeys ?? [];
        var slotRules = new[] { presentation.Side, presentation.Main }.OfType<UiChildRef>().Concat(views.Where(v => v?.Root is not null).Select(view => view.Root))
            .Select(slot => (Rule)new SlotNamesABrainNeuron(string.IsNullOrWhiteSpace(slot.Kind) ? "" : slot.Name, State.BrainScopeId ?? ""));
        await Check("Present",
            [new PresentationHasViews(viewCount), .. slotRules, new BlackboardKeysAreDeclarable(declaredKeys)]);
        if (presentation.Sphere?.Tree is { } tree) { FlameSchema.CheckTree(tree, declaredKeys); }
        if (new PresentationCollaborator(State).Publish(presentation) is { } next)
        {
            await Save(next, new PresentationChanged(Key, next.ProgramRevision, next.Presentation!.Revision));
        }
        return Describe(State);
    }

    private Task RequireExecutingAssignment(string operation, long expectedProgramRevision)
    {
        if (!RuntimeCallerContext.TryGet(out var caller) || caller.Kind != CallerKind.App
            || !string.Equals(caller.AppId, Key, StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("Only this app's executing assignment may publish its presentation."); }
        return Check(operation,
            new AppExists(Name(), State.Manifest is not null),
            new AssignmentIsCurrent(expectedProgramRevision != State.ProgramRevision));
    }
}
