/*
# Workspace
Role: Shell
Shipped: true
Operations: open, navigate, activate

The embedded folder scripts publish the Flutter workspace. These operations present a status for the baseline.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Workspace;

[Signal("workspace.open")]
public sealed record WorkspaceOpen : Signal;

[Signal("workspace.navigate")]
public sealed record WorkspaceNavigate : Signal;

[Signal("workspace.activate")]
public sealed record WorkspaceActivate : Signal;

public sealed record WorkspaceAppState(string Status = "Ready")
{
    public WorkspaceAppState() : this("Ready")
    {
    }
}

[Neuron("app.workspace")]
public sealed class WorkspaceApp : Screen<WorkspaceAppState>
{
    public const string AppName = "workspace";

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

    [On]
    public Task Open(WorkspaceOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State);
    }

    [On]
    public Task Navigate(WorkspaceNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State);
    }

    [On]
    public Task Activate(WorkspaceActivate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State);
    }

    public new WorkspaceAppState State => base.State;
    public View Render() => Render(State);
    protected override View Render(WorkspaceAppState state) => new("Workspace", new Text(state.Status));
}
