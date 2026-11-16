using DigitalBrain.Capabilities;

namespace IntoChat.OS.Modules.Apps;

// VfsNeuron also builds its WorkspaceFilesService itself rather than getting a shared one, so this neuron builds one per
// activation (not per call). Files that live in a connected storage are always visible; files written only into another
// service instance's in-memory overlay (for example the compiled-app host's cached instance) with no storage connected
// are not visible here.
[DigitalBrain.Neurons.Neuron("ino.files")]
public sealed class InoFilesNeuron(ICapabilityBroker? broker = null, IServiceProvider? services = null) : DigitalBrain.Neurons.Neuron, IInoFiles
{
    private IFilesVfsService? _vfs;

    private IFilesVfsService Vfs => _vfs ??= WorkspaceFilesService.ForBrain(services, Id.Brain.Value, broker);

    public async Task<InoToolResult> Find(string pattern)
    {
        var vfs = Vfs;
        var files = await vfs.ListFilesAsync(string.IsNullOrWhiteSpace(pattern) ? null : pattern);
        if (files.Count == 0)
        {
            return new InoToolResult($"No files match '{pattern}'.", FilesCard("Files Workspace (0 item(s))", "Connected to storage.files", Id.Brain));
        }

        var lines = string.Join("\n", files.Take(12).Select(f => $"- {f.Name} ({f.NeuronUri} · {f.Kind}{(f.Size is { } size ? $" · {size:N0} bytes" : string.Empty)})"));
        var card = FilesCard($"Files Workspace ({files.Count} item(s))", string.Join(", ", files.Take(5).Select(f => f.Name)), Id.Brain);
        return new InoToolResult($"Found {files.Count} file(s):\n{lines}", card);
    }

    public async Task<InoToolResult> Open(string path)
    {
        var vfs = Vfs;
        var file = await vfs.ResolveFileAsync(path);
        if (file is null)
        {
            return new InoToolResult($"No file found for '{path}'.");
        }

        var text = await vfs.ReadTextAsync(file.Name);
        var summary = Excerpt(text) ?? $"Workspace file resource attached from Files ({file.NeuronUri})";
        return new InoToolResult($"Opened {file.Name} ({file.NeuronUri}).", FileCard(file, summary, Id.Brain));
    }

    public async Task<InoToolResult> Summarize(string path)
    {
        var vfs = Vfs;
        var file = await vfs.ResolveFileAsync(path);
        var text = file is null ? null : await vfs.ReadTextAsync(file.Name);
        if (file is null || string.IsNullOrWhiteSpace(text))
        {
            return new InoToolResult($"No readable text found for '{path}'.");
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var excerpt = string.Join("\n", lines.Take(6).Select(l => $"> {l}"));
        return new InoToolResult($"{file.Name} ({file.NeuronUri}): {lines.Length} non-empty line(s), {text.Length} chars.\n{excerpt}", FileCard(file, Excerpt(text)!, Id.Brain));
    }

    private static string? Excerpt(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 180 ? text : text[..180] + "…";

    private static InoCard FileCard(WorkspaceFileEntry file, string summary, DigitalBrain.BrainId brain)
        => new($"file-{file.Name}", "file", file.Name, summary, file.NeuronUri, "ready", $"neuron://{IntoChat.OS.Primitives.AppRef.Of(brain, "ino").Key}#file-{file.Name}");

    private static InoCard FilesCard(string title, string summary, DigitalBrain.BrainId brain)
        => new("files-find", "file", title, summary, $"neuron://{IntoChat.OS.Primitives.AppRef.Of(brain, "files").Key}", "ready", $"neuron://{IntoChat.OS.Primitives.AppRef.Of(brain, "ino").Key}#files-find");
}
