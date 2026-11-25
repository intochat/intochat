using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps;

// The brain's workspace addressed by path: "reports/q4.csv" inside the workspace storage (the cloud module's
// local-cloud/main connection). It is reached through the broker under the caller the runtime stamped, by its connection
// key (never by guessing from a key's text), and a path is walked segment by segment (never matched by file name
// elsewhere). The old mount prefix "/work/" is accepted and dropped, so stored paths keep working; "/local/" and
// "/drive/" named other storages and are refused. Nothing is kept between calls.
internal sealed class BrainWorkspaceFiles(ICapabilityBroker broker, BrainId brain)
{
    public const long MaxReadBytes = 16 * 1024 * 1024;
    private const string LegacyMount = "work";

    public static string ConnectionKey(BrainId brain) => new NeuronId(brain, CloudModule.WorkspaceNeuronType, "main").ToString();

    // The workspace-relative form of a path: "" for the workspace itself.
    public static string Normalize(string? path)
    {
        if (path is null) { throw new ArgumentNullException(nameof(path)); }
        if (path.Contains('\0', StringComparison.Ordinal)) { throw new ArgumentException("The path is not valid.", nameof(path)); }
        var text = path.Trim().Replace('\\', '/');
        if (text.StartsWith('/'))
        {
            text = text.TrimStart('/');
            var first = text.Split('/', 2)[0];
            if (string.Equals(first, LegacyMount, StringComparison.OrdinalIgnoreCase)) { text = text.Length > first.Length ? text[(first.Length + 1)..] : ""; }
            else if (first is "local" or "drive") { throw new ArgumentException($"'/{first}' is not the workspace.", nameof(path)); }
        }
        text = text.Trim('/');
        if (text.Length == 0) { return ""; }
        return SafeRelativePath.TryParse(text, out var safe) ? safe.Value : throw new ArgumentException("The path is not valid.", nameof(path));
    }

    // The form apps keep in their stored state ("/work/reports/q4.csv"); Normalize takes it back.
    public static string Stored(string path) => "/" + LegacyMount + "/" + Normalize(path);

    // The stored form of the file at the path when there is one, else null.
    public async Task<string?> Located(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        string relative;
        try { relative = Normalize(path); }
        catch (ArgumentException) { return null; }
        return await Find(relative, ct).ConfigureAwait(true) is { Kind: ItemKind.File } ? Stored(relative) : null;
    }

    public async Task<IStorage> Storage(CancellationToken ct = default)
    {
        var key = ConnectionKey(brain);
        var connections = await broker.ResolveAll<IStorage>(brain, ct).ConfigureAwait(true);
        return connections.FirstOrDefault(connection => string.Equals(connection.ConnectionKey, key, StringComparison.Ordinal))?.Capability
            ?? throw new InvalidOperationException("This brain's workspace is not available.");
    }

    // The item at the path, or null.
    public async Task<Item?> Find(string path, CancellationToken ct = default)
        => await Walk(await Storage(ct).ConfigureAwait(true), Normalize(path), ct).ConfigureAwait(true);

    // The items in the folder at the path (empty when there is none).
    public async Task<IReadOnlyList<Item>> List(string folder, CancellationToken ct = default)
    {
        var storage = await Storage(ct).ConfigureAwait(true);
        return await Walk(storage, Normalize(folder), ct).ConfigureAwait(true) is { Kind: ItemKind.Folder } item
            ? await All(storage, item.Id, ct).ConfigureAwait(true)
            : [];
    }

    // Every file of the workspace whose name contains the text (the storage's own search), with its workspace path.
    public async Task<IReadOnlyList<(string Path, Item Item)>> Search(string? text, CancellationToken ct = default)
    {
        var storage = await Storage(ct).ConfigureAwait(true);
        var found = new List<(string, Item)>();
        string? cursor = null;
        do
        {
            var page = await storage.Search(new SearchQuery(text?.Trim() ?? "", Cursor: cursor), ct).ConfigureAwait(true);
            foreach (var item in page.Items.Where(item => item.Kind == ItemKind.File))
            {
                // The path is the names of the folders above the file; the root's own name is not part of it.
                var above = await storage.Ancestors(item.Id, ct).ConfigureAwait(true);
                found.Add((string.Join('/', above.Skip(1).Select(folder => folder.Name).Append(item.Name)), item));
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null && found.Count < 500);
        return found;
    }

    public async Task<byte[]?> Read(string path, long maxBytes = MaxReadBytes, CancellationToken ct = default)
    {
        var storage = await Storage(ct).ConfigureAwait(true);
        if (await Walk(storage, Normalize(path), ct).ConfigureAwait(true) is not { Kind: ItemKind.File } item) { return null; }
        return (await storage.Read(item.Id, new ByteRange(0, maxBytes), ct).ConfigureAwait(true)).Bytes.ToArray();
    }

    // Writes a file into an existing folder and returns it once it is there (a stream committed, not an operation queued).
    public async Task<Item> Write(string path, ReadOnlyMemory<byte> content, ConflictPolicy conflict = ConflictPolicy.Overwrite, CancellationToken ct = default)
    {
        var relative = Normalize(path);
        if (relative.Length == 0) { throw new ArgumentException("The path names no file.", nameof(path)); }
        var slash = relative.LastIndexOf('/');
        var (folderPath, name) = slash < 0 ? ("", relative) : (relative[..slash], relative[(slash + 1)..]);
        var storage = await Storage(ct).ConfigureAwait(true);
        var folder = await Walk(storage, folderPath, ct).ConfigureAwait(true);
        if (folder is not { Kind: ItemKind.Folder }) { throw new DirectoryNotFoundException("The folder of that path does not exist."); }
        if (await Child(storage, folder.Id, name, ct).ConfigureAwait(true) is { Kind: ItemKind.Folder }) { throw new ArgumentException("That path is a folder.", nameof(path)); }
        var plan = WritePlan.WriteFile(folder.Id, name, ReadOnlyMemory<byte>.Empty, conflict) with { Inline = null };
        await using var writer = await storage.OpenWrite(plan, ct).ConfigureAwait(true);
        await writer.Content.WriteAsync(content, ct).ConfigureAwait(true);
        var id = await writer.Commit(ct).ConfigureAwait(true);
        return await storage.Stat(id, ct).ConfigureAwait(true);
    }

    private static async Task<Item?> Walk(IStorage storage, string relative, CancellationToken ct)
    {
        var roots = await storage.Roots(ct).ConfigureAwait(true);
        if (roots.Count == 0) { return null; }
        Item current = roots[0];
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Kind != ItemKind.Folder) { return null; }
            if (await Child(storage, current.Id, segment, ct).ConfigureAwait(true) is not { } next) { return null; }
            current = next;
        }
        return current;
    }

    private static async Task<Item?> Child(IStorage storage, ItemId folder, string name, CancellationToken ct)
    {
        try { return (await All(storage, folder, ct).ConfigureAwait(true)).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal)); }
        catch (NotFound) { return null; }
    }

    private static async Task<IReadOnlyList<Item>> All(IStorage storage, ItemId folder, CancellationToken ct)
    {
        var items = new List<Item>();
        string? cursor = null;
        do
        {
            var page = await storage.List(folder, new ListQuery(Cursor: cursor, PageSize: 200), ct).ConfigureAwait(true);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null && items.Count < 10_000);
        return items;
    }
}
