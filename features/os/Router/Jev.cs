using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using IntoChat.OS.Modules.Microsoft.Windows;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;

// Shared by every synapse of the router: the app's key and brain, its neuron names, and the
// capabilities Jev routes over. A synapse is one reaction; this is the vocabulary they speak.
public static class Jev
{
    public const string Unclear = "The request was not clear enough to route.";

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain) => brain.Setting("Brain")!;
    public static string Name(IntoChatOSConnection brain, string part) => Key(brain) + "/" + part;

    // What Jev may act on: timers, notes and C# always, and the PC's windows, console and files once this
    // brain has a windows connection. A capability the brain cannot reach is left out, because every
    // extra option dilutes Jev's choice. App methods are never offered: an app is driven by its own
    // operations, which is the assistant's job, and a raw IApp call could delete or roll back an app.
    public static async Task<string[]> Capabilities(IntoChatOSConnection brain, CancellationToken ct)
    {
        string[] direct = [typeof(ITimer).FullName!, typeof(IText).FullName!, typeof(ICSharpFile).FullName!];
        await foreach (var _ in brain.Kernel.Instances<IWindows>(ct))
        { return [.. direct, typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!]; }
        return direct;
    }

    // Focuses the router, whose main layout includes its timer status.
    public static async Task Show(IntoChatOSConnection brain)
    {
        var workspace = brain.Get<IWorkspace>(Scope(brain));
        await workspace.Show(Key(brain), Guid.NewGuid().ToString(), (await workspace.Read()).Revision);
    }

    public static string Short(string contract) => contract[(contract.LastIndexOf('.') + 1)..];

    public static string Clip(string text, int max = 2000) => text.Length <= max ? text : text[..max] + "…";
}
