using Microsoft.Extensions.Logging;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.Apps;

// Ino's file tools. Find, Open and Summarize read the brain's workspace by path and the other connected storages by
// their own search; a file outside the workspace is named by its storage and id ("storage:<connection>#<id>"), so the
// tools never guess a file by its name. Write creates a file in the owner's Documents folder on the local disk, the
// connection named by its key ({brain}/local-disk/main), after the owner approves.
[DigitalBrain.Neurons.Neuron("ino.files")]
internal sealed class InoFilesNeuron(ICapabilityBroker? broker = null, IServiceProvider? services = null, IOperations? operations = null)
    : DigitalBrain.Neurons.Neuron<InoFilesState>, IFiles
{
    private const int KeptWrites = 32;
    private const int Shown = 12;
    private const string Elsewhere = "storage:";

    private ILogger Logger =>
        (ILogger?)(services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<InoFilesNeuron>()
        ?? global::IntoChat.OS.Host.Logging.OsConsoleLog.For<InoFilesNeuron>();

    private ICapabilityBroker Broker => broker ?? throw new InvalidOperationException("No storage is connected in this brain.");

    private BrainWorkspaceFiles Workspace => new(Broker, Id.Brain);

    internal static string LocalDiskKey(DigitalBrain.BrainId brain) => new DigitalBrain.NeuronId(brain, "local-disk", "main").ToString();

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (State.Writes.Length > 0)
        {
            await Context.Wires.EnsureBrainWide(typeof(OperationDone), typeof(ConversationChanged), typeof(TurnCompleted));
        }
    }

    public async Task<InoToolResult> Find(string pattern)
    {
        var text = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
        var found = new List<(string Reference, string Name, string Where, long? Size)>();
        foreach (var (path, item) in await Workspace.Search(text))
        {
            found.Add((path, item.Name, "workspace", item.Size));
        }
        if (text is not null)
        {
            var workspaceKey = BrainWorkspaceFiles.ConnectionKey(Id.Brain);
            foreach (var connection in await Broker.ResolveAll<IStorage>(Id.Brain, CancellationToken.None))
            {
                if (connection.ConnectionKey == workspaceKey) { continue; }
                try
                {
                    var page = await connection.Capability.Search(new SearchQuery(text), CancellationToken.None);
                    found.AddRange(page.Items.Where(item => item.Kind == ItemKind.File)
                        .Select(item => (Elsewhere + connection.ConnectionKey + "#" + item.Id.Value, item.Name, connection.Label, item.Size)));
                }
                // One storage that cannot search does not hide the others; it is logged so telemetry shows it.
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Logger.LogWarning(error, "Searching {Connection} for Ino's files_find failed.", connection.ConnectionKey);
                }
            }
        }
        if (found.Count == 0)
        {
            return new InoToolResult($"No files match '{pattern}'.", FilesCard("Files (0 item(s))", "Workspace and connected storages.", Id.Brain));
        }

        var lines = string.Join("\n", found.Take(Shown).Select(f => $"- {f.Name} ({f.Where} · {f.Reference}{(f.Size is { } size ? $" · {size:N0} bytes" : string.Empty)})"));
        var card = FilesCard($"Files ({found.Count} item(s))", string.Join(", ", found.Take(5).Select(f => f.Name)), Id.Brain);
        return new InoToolResult($"Found {found.Count} file(s); open one by the reference after the dot:\n{lines}", card);
    }

    public async Task<InoToolResult> Open(string path)
    {
        if (await ReadAsync(path) is not { } file) { return new InoToolResult($"No file found at '{path}'. Use files_find to look it up."); }
        var summary = Excerpt(file.Text) ?? $"File {file.Name} from {file.Where}";
        return new InoToolResult($"Opened {file.Name} ({file.Where} · {file.Reference}).", FileCard(file.Name, file.Reference, summary, Id.Brain));
    }

    public async Task<InoToolResult> Summarize(string path)
    {
        var file = await ReadAsync(path);
        if (file is not { } read || string.IsNullOrWhiteSpace(read.Text))
        {
            return new InoToolResult($"No readable text found for '{path}'.");
        }

        var text = read.Text;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var excerpt = string.Join("\n", lines.Take(6).Select(l => $"> {l}"));
        return new InoToolResult($"{read.Name} ({read.Reference}): {lines.Length} non-empty line(s), {text.Length} chars.\n{excerpt}", FileCard(read.Name, read.Reference, Excerpt(text)!, Id.Brain));
    }

    // A workspace path, or the reference files_find gave for a file of another storage. Nothing else is tried.
    private async Task<(string Name, string Reference, string Where, string? Text)?> ReadAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }
        var trimmed = reference.Trim();
        var hash = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (trimmed.StartsWith(Elsewhere, StringComparison.Ordinal) && hash > Elsewhere.Length)
        {
            var key = trimmed[Elsewhere.Length..hash];
            var connection = (await Broker.ResolveAll<IStorage>(Id.Brain, CancellationToken.None)).FirstOrDefault(item => item.ConnectionKey == key);
            if (connection is null) { return null; }
            var id = new ItemId(trimmed[(hash + 1)..]);
            try
            {
                var item = await connection.Capability.Stat(id, CancellationToken.None);
                if (item.Kind != ItemKind.File) { return null; }
                var content = await connection.Capability.Read(id, new ByteRange(0, BrainWorkspaceFiles.MaxReadBytes), CancellationToken.None);
                return (item.Name, trimmed, connection.Label, Text(content.Bytes.Span));
            }
            catch (StorageException error)
            {
                Logger.LogInformation(error, "Ino's file reference {Reference} could not be read.", trimmed);
                return null;
            }
        }

        string relative;
        try { relative = BrainWorkspaceFiles.Normalize(trimmed); }
        catch (ArgumentException) { return null; }
        if (relative.Length == 0) { return null; }
        var bytes = await Workspace.Read(relative);
        if (bytes is null) { return null; }
        return (relative[(relative.LastIndexOf('/') + 1)..], relative, "workspace", Text(bytes));
    }

    private static string? Text(ReadOnlySpan<byte> bytes) => bytes.Contains((byte)0) ? null : System.Text.Encoding.UTF8.GetString(bytes);

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
        var diskKey = LocalDiskKey(Id.Brain);
        var disk = connections.FirstOrDefault(item => string.Equals(item.ConnectionKey, diskKey, StringComparison.Ordinal))?.Capability
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

    private static InoCard FileCard(string name, string reference, string summary, DigitalBrain.BrainId brain)
        => new($"file-{name}", "file", name, summary, reference, "ready", $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "ino").Key}#file-{name}");

    private static InoCard FilesCard(string title, string summary, DigitalBrain.BrainId brain)
        => new("files-find", "file", title, summary, $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "files").Key}", "ready", $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(brain, "ino").Key}#files-find");
}
