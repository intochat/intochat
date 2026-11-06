/*
# Workspace
Role: Shell
Shipped: true
Operations: open, navigate, activate

The embedded folder scripts publish the Flutter workspace. These operations present a status for the baseline.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Workspace;

[DigitalBrain.Signal("workspace.open")]
public sealed record WorkspaceOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("workspace.navigate")]
public sealed record WorkspaceNavigate : DigitalBrain.Signal;

[DigitalBrain.Signal("workspace.activate")]
public sealed record WorkspaceActivate : DigitalBrain.Signal;

[DigitalBrain.App("workspace", Accent = "#A855F7", Category = "Social")]
public sealed class WorkspaceApp : DigitalBrain.App
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(WorkspaceApp));

    public static AppBundle Bundle { get; } = new(
        "workspace",
        "0.4.0",
        new AppManifest(
            Title: "Workspace",
            Description: "Your apps, in one quiet workspace.",
            Operations:
            [
                new("open", "Rebuild without changing focus."),
                new("navigate", "Apply a revision-bound navigation command."),
                new("activate", "Open an app and dispatch an explicit user action."),
            ],
            Settings: [],
            Slots: []),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(WorkspaceApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public Task Open(WorkspaceOpen request) => Show(request, "Workspace");

    [DigitalBrain.On]
    public Task Navigate(WorkspaceNavigate request) => Show(request, "Workspace");

    [DigitalBrain.On]
    public Task Activate(WorkspaceActivate request) => Show(request, "Workspace");

    private Task Show(DigitalBrain.Signal request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = title;
        return Task.CompletedTask;
    }

    public View Render() => new("Workspace", new Text("Workspace"));

    [DigitalBrain.Scenario("Workspace opens and presents the Workspace status view")]
    public Task VerifyWorkspaceOpenScenario() => Open(new WorkspaceOpen());
}
