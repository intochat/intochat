using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Log;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.LocalDisk;

// OS module 'cloud': filtered local cloud storage over LocalCloudNeuron.
[Module(Id)]
public sealed class CloudModule : Module
{
    public const string Id = "cloud";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}

public sealed class LocalCloudState
{
    public string? RootDirectory { get; set; }
    public long MaxStorageBytes { get; set; } = LocalCloudNeuron.DefaultMaxStorageBytes;
    public Dictionary<string, LocalCloudEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed record LocalCloudEntry(
    string Path,
    string Name,
    ItemKind Kind,
    byte[] Content,
    DateTimeOffset Modified,
    string ContentType = "application/octet-stream");

// Filtered local IStorage neuron for the 'cloud' OS module.
// Exposes safe workspace/cloud roots, filters out hidden/dot files (.git, .env, .vs, secrets) and blocks any '..' path traversal.
[Neuron(TypeName)]
public sealed class LocalCloudNeuron : Neuron<LocalCloudState>, IStorage
{
    public const string TypeName = "local-cloud";
    public const string RootIdValue = "cloud";
    public const string RootDisplayName = "Local Cloud";
    public const long DefaultMaxStorageBytes = 1024L * 1024 * 1024; // 1 GiB per brain

    private const ItemFlags Offered = ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder
        | ItemFlags.Copy | ItemFlags.Move | ItemFlags.Rename | ItemFlags.Delete;

    private static readonly HashSet<string> BlockedSegmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".env",
        ".vs",
        ".vscode",
        ".idea",
        ".ssh",
        ".aws",
        ".azure",
        ".npmrc",
        ".pypirc",
        ".DS_Store",
        "secrets",
        "secrets.json",
        "appsettings.secrets.json",
    };

    private static readonly string[] BlockedExtensions =
    [
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".kdbx",
        ".env",
    ];

    private readonly LocalCloudState _standaloneState = new();
    private readonly TimeProvider? _time;

    public LocalCloudNeuron()
    {
    }

    public LocalCloudNeuron(string? rootDirectory, TimeProvider? time = null)
    {
        _time = time;
        ConfigureRootDirectory(rootDirectory);
    }

    private DateTimeOffset UtcNow()
    {
        if (_time is not null)
        {
            return _time.GetUtcNow();
        }

        try
        {
            return Context.Time.GetUtcNow();
        }
        catch (InvalidOperationException error)
        {
            OsConsoleLog.For<LocalCloudNeuron>().LogSwallowed(error, "LocalCloudNeuron is detached; using system clock.");
            return TimeProvider.System.GetUtcNow();
        }
    }

    internal static string StorageDisplayName => RootDisplayName;
    internal static ItemFlags StorageOffered => Offered;

    private LocalCloudStorage Storage => new(() => ActiveState, UtcNow);

    public long MaxStorageBytes
    {
        get => Storage.MaxStorageBytes;
        set => Storage.MaxStorageBytes = value;
    }

    private LocalCloudState ActiveState
    {
        get
        {
            try
            {
                return State ?? _standaloneState;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<LocalCloudNeuron>().LogSwallowed(error, "Reading LocalCloud grain state failed; using standalone state.");
                return _standaloneState;
            }
        }
    }

    public void ConfigureRootDirectory(string? rootDirectory) => Storage.ConfigureRootDirectory(rootDirectory);

    public static bool IsTraversalOrInvalid(string? path) => LocalCloudStorage.IsTraversalOrInvalid(path);

    public static bool IsHiddenOrSecretSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) { return false; }
        var trimmed = segment.Trim();
        if (trimmed.StartsWith('.')) { return true; }
        if (BlockedSegmentNames.Contains(trimmed)) { return true; }
        foreach (var ext in BlockedExtensions)
        {
            if (trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    public static bool IsFilteredPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) { return false; }
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || string.Equals(normalized, RootIdValue, StringComparison.Ordinal)) { return false; }
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsHiddenOrSecretSegment(segment)) { return true; }
        }
        return false;
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default) => Storage.Describe(ct);
    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => Storage.Roots(ct);
    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => Storage.List(folder, query, ct);
    public Task<Item> Stat(ItemId id, CancellationToken ct = default) => Storage.Stat(id, ct);
    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => Storage.Read(id, range, ct);
    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => Storage.Write(plan, ct);
    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => Storage.Search(query, ct);
    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => Storage.Ancestors(id, ct);
    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => Storage.OpenRead(item, range, ct);
    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => Storage.OpenWrite(plan, ct);
}
