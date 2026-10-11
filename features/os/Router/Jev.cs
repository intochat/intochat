using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.Windows;
using ITimer = DigitalBrain.Time.Timers.ITimer;

// Shared by every synapse of the router: the app's key and brain, its neuron names, and the
// capabilities Jev routes over. A synapse is one reaction; this is the vocabulary they speak.
public static class Jev
{
    public const string Unclear = "The request was not clear enough to route.";

    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;
    public static string Name(DigitalBrainConnection brain, string part) => Key(brain) + "/" + part;

    // What Jev may act on: timers, notes and C# always, and the PC's windows, console and files once this
    // brain has a windows connection. A capability the brain cannot reach is left out, because every
    // extra option dilutes Jev's choice. App methods are never offered: an app is driven by its own
    // operations, which is the assistant's job, and a raw IApp call could delete or roll back an app.
    public static async Task<string[]> Capabilities(DigitalBrainConnection brain, CancellationToken ct)
    {
        string[] direct = [typeof(ITimer).FullName!, typeof(IText).FullName!, typeof(ICSharpFile).FullName!];
        await foreach (var _ in brain.Kernel.Instances<IWindows>(ct))
        { return [.. direct, typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!]; }
        return direct;
    }

    // Opens a window on one of the router's surfaces unless it is already open. Another writer can move the
    // workspace's revision between the read and the open; the script client reports that conflict as a
    // plain refusal (422, "Expected revision …"), so EnsureOpenAsync's own conflict retry never sees it, and
    // a fresh read settles it. Any other refusal (a timeout is one too) is not retried.
    public static async Task OpenWindow(DigitalBrainConnection brain, string window, string title, string surface)
    {
        var workspace = brain.Get<IWorkspace>(Scope(brain));
        var reference = WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Name(brain, surface)));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await workspace.EnsureOpenAsync(Name(brain, window), title, reference, brain.Stopping);
                return;
            }
            catch (InvalidOperationException conflict) when (attempt < 4 && conflict.Message.Contains("Expected revision", StringComparison.Ordinal)) { }
        }
    }

    public static string Short(string contract) => contract[(contract.LastIndexOf('.') + 1)..];

    public static string Clip(string text, int max = 2000) => text.Length <= max ? text : text[..max] + "…";
}
