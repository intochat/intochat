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
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Workspace;

[Signal("workspace.open")]
public sealed record WorkspaceOpen : Signal;

[Signal("workspace.navigate")]
public sealed record WorkspaceNavigate : Signal;

[Signal("workspace.activate")]
public sealed record WorkspaceActivate : Signal;

public sealed record WorkspaceAppState(
    string Status = "Ready",
    string ActiveSurface = "workspace",
    int Revision = 0)
{
    public WorkspaceAppState() : this("Ready", "workspace", 0)
    {
    }

    [Invariant]
    public bool NonNegativeRevision => Revision >= 0 && !string.IsNullOrWhiteSpace(Status);

    public WorkspaceAppState Rebuild() => this with
    {
        Status = string.IsNullOrWhiteSpace(Status) ? "Ready" : Status,
    };

    public WorkspaceAppState NavigateTo([Sample("desktop", "tasks")] string? surface = null) => this with
    {
        ActiveSurface = string.IsNullOrWhiteSpace(surface) ? ActiveSurface : surface.Trim().ToLowerInvariant(),
        Revision = Revision + 1,
        Status = "Ready",
    };

    public WorkspaceAppState ActivateSurface([Sample("ino", "creator")] string? app = null) => this with
    {
        ActiveSurface = string.IsNullOrWhiteSpace(app) ? ActiveSurface : app.Trim().ToLowerInvariant(),
        Revision = Revision + 1,
        Status = "Active",
    };
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
        return Show(State.Rebuild());
    }

    [On]
    public Task Navigate(WorkspaceNavigate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State.NavigateTo());
    }

    [On]
    public Task Activate(WorkspaceActivate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State.ActivateSurface());
    }

    public new WorkspaceAppState State => base.State;
    public View Render() => Render(State);

    protected override View Render(WorkspaceAppState state) => new(
        "Workspace",
        Surface(SurfaceKind.Elevated, "Workspace", "Your apps, in one quiet workspace.")[
            HStack(Space.Sm)[
                StatusPill(state.Status == "Active" ? StatusKind.Working : StatusKind.Done, state.Status),
                MutedText($"Surface: {state.ActiveSurface} · rev {state.Revision}")
            ]
        ]);
}
