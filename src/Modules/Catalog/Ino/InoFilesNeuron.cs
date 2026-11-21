using Microsoft.Extensions.Logging;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.Apps;

// VfsNeuron also builds its WorkspaceFilesService itself rather than getting a shared one, so this neuron builds one per
// activation (not per call). Files that live in a connected storage are always visible; files written only into another
// service instance's in-memory overlay (for example the compiled-app host's cached instance) with no storage connected
// are not visible here.
[DigitalBrain.Neurons.Neuron("ino.files")]
internal sealed class InoFilesNeuron(ICapabilityBroker? broker = null, IServiceProvider? services = null, IOperations? operations = null)
    : DigitalBrain.Neurons.Neuron<InoFilesState>, IFiles
{
    private const int KeptWrites = 32;

    private ILogger Logger =>
        (ILogger?)(services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<InoFilesNeuron>()
        ?? global::IntoChat.OS.Host.Logging.OsConsoleLog.For<InoFilesNeuron>();

    private IFilesVfsService? _vfs;

    private IFilesVfsService Vfs => _vfs ??= WorkspaceFilesService.ForBrain(services, Id.Brain.Value, broker);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (State.Writes.Length > 0)
        {
            await Context.Wires.EnsureBrainWide(typeof(OperationDone), typeof(ConversationChanged), typeof(TurnCompleted));
        }
    }

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

    // A write is a durable storage operation: it waits for the owner's approval (across restarts), runs, and ends with
    // OperationDone. This neuron remembers which conversation asked and posts the outcome there; it never writes itself.
    public async Task<InoToolResult> Write(string name, string content)
    {
        var broken = InoFileWriteRules.Check(name, content);
        if (broken is not null) { return new InoToolResult(broken); }
        if (operations is null) { throw new InvalidOperationException("Durable operations are not available in this host."); }

        // Storage is reached through the broker only (D-18); the local disk is the connection whose key names it.
        if (broker is null) { throw new InvalidOperationException("No storage is connected in this brain."); }
        var connections = await broker.ResolveAll<IStorage>(Id.Brain, CancellationToken.None);
        var disk = connections.FirstOrDefault(item => item.ConnectionKey.Contains("local-disk", StringComparison.OrdinalIgnoreCase))?.Capability
            ?? throw new InvalidOperationException("The local disk is not connected in this brain.");
        var roots = await disk.Roots();
        var root = InoFileWriteRules.Target(roots)
            ?? throw new InvalidOperationException("No writable local folder is allowed. Allow one in Files first.");

        var conversation = Context.Caller?.Conversation ?? string.Empty;
        // An agent never creates a file unasked: RequiresPerson makes even a new, non-destructive file ask the owner.
        var plan = WritePlan.WriteFile(root.Id, name, System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty), ConflictPolicy.Fail)
            with
        { RequiresPerson = true };

        await Context.Wires.EnsureBrainWide(typeof(OperationDone), typeof(ConversationChanged), typeof(TurnCompleted));
        var operation = await operations.Start(plan, CancellationToken.None);
        var write = new InoFileWrite(operation.Id.Value, conversation, name, root.Name);
        await Save(State with { Writes = [.. State.Writes.TakeLast(KeptWrites - 1), write] },
            new InoFileWriteRequested(write.Operation, conversation, name));
        return new InoToolResult($"Asked the owner to approve creating {name} in {root.Name}. Nothing is written until they approve; the result will be posted here.");
    }

    [DigitalBrain.Neurons.On]
    public async Task OnOperationDone(OperationDone done)
    {
        if (InoFileWriteReports.Settle(State, done) is not { } settled) { return; }
        await Save(settled.Next, new InoFileWriteReported(done.Operation.Value, settled.Outcome));
        await Report();
    }

    // A post fails while the conversation is answering; the next ConversationChanged (or legacy TurnCompleted) retries every unposted outcome.
    [DigitalBrain.Neurons.On]
    public Task OnConversationChanged(ConversationChanged changed) => changed is null ? Task.CompletedTask : Report();

    [DigitalBrain.Neurons.On]
    public Task OnTurnCompleted(TurnCompleted completed) => completed is null ? Task.CompletedTask : Report();

    private async Task Report()
    {
        foreach (var write in InoFileWriteReports.Unposted(State))
        {
            try
            {
                var conversationKey = InoFileWriteReports.ConversationKey(Id.Brain.Value, write.Conversation);
                await Context.Get<IConversation>(new DigitalBrain.NeuronId(Id.Brain, IConversation.Type, conversationKey))
                    .Post(write.Outcome!, "file/" + write.Operation);
            }
            // Left unposted on any failure, so the next conversation update tries again; the failure is logged so telemetry shows it.
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logger.LogWarning(error, "Posting the outcome of file operation {Operation} into {Conversation} failed; it stays pending.", write.Operation, write.Conversation);
                continue;
            }
            await Save(State with { Writes = [.. State.Writes.Where(item => item.Operation != write.Operation)] },
                new InoFileWriteReported(write.Operation, "posted"));
        }
    }

    private static string? Excerpt(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 180 ? text : text[..180] + "…";

    private static InoCard FileCard(WorkspaceFileEntry file, string summary, DigitalBrain.BrainId brain)
        => new($"file-{file.Name}", "file", file.Name, summary, file.NeuronUri, "ready", $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "ino").Key}#file-{file.Name}");

    private static InoCard FilesCard(string title, string summary, DigitalBrain.BrainId brain)
        => new("files-find", "file", title, summary, $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "files").Key}", "ready", $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "ino").Key}#files-find");
}
