namespace DigitalBrain.Flutter.Workspace;

public static class WorkspaceWindows
{
    // Opens a typed window unless it is already open on the same reference.
    public static async Task EnsureOpenAsync(this IWorkspace workspace, string windowId, string title, WindowReference reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = await workspace.Read().WaitAsync(ct);
        if (state.Windows.Any(window => window.Id == windowId && window.IsOpen && window.Reference == reference)) { return; }
        await workspace.Open(new(Guid.NewGuid().ToString(), windowId, title, reference, state.Revision)).WaitAsync(ct);
    }
}
