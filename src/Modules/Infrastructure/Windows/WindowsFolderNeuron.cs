using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Microsoft.Windows;

[NeuronAlias("windows.folder-signals"), DefaultNeuronType(IWindowsFolderGateway.TypeName)]
internal interface IWindowsFolderSignals
{
    Task PublishFileChangedAsync(FileChanged change, CancellationToken cancellationToken = default);
}

[Neuron(IWindowsFolderGateway.TypeName)]
internal sealed class WindowsFolderNeuron(WindowsFolderWatcher? watcher = null)
    : Neuron<WindowsFolderState>, IWindowsFolderGateway, IWindowsFolderSignals
{
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        watcher?.SyncBrain(Id.Brain.Value, State.Grants);
    }

    public async Task GrantSlotAsync(WindowsFolderGrant grant, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdministrativeCaller();
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.HostPath);

        var normalizedHostPath = PathConfinement.Normalize(grant.HostPath);
        if (!Directory.Exists(normalizedHostPath))
        {
            throw new DirectoryNotFoundException($"Host folder '{normalizedHostPath}' does not exist.");
        }

        var normalizedGrant = grant with
        {
            AppKey = grant.AppKey.Trim(),
            Slot = grant.Slot.Trim(),
            HostPath = normalizedHostPath,
        };

        var updated = State.Grants
            .Where(existing => !MatchesGrant(existing, normalizedGrant.AppKey, normalizedGrant.Slot))
            .Append(normalizedGrant)
            .ToArray();

        var nextState = new WindowsFolderState { Grants = updated };
        await Store(nextState);
        watcher?.SyncBrain(Id.Brain.Value, nextState.Grants);
    }

    public async Task RevokeAppAsync(string appKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdministrativeCaller();
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var normalizedAppKey = appKey.Trim();
        var updated = State.Grants
            .Where(existing => !string.Equals(existing.AppKey, normalizedAppKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (updated.Length == State.Grants.Length)
        {
            return;
        }

        var nextState = new WindowsFolderState { Grants = updated };
        await Store(nextState);
        watcher?.SyncBrain(Id.Brain.Value, nextState.Grants);
    }

    public Task<WindowsFolderGrant[]> ListGrantsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdministrativeCaller();
        return Task.FromResult(State.Grants);
    }

    public Task<FolderEntry[]> ListAsync(string slot, string relativePath = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (normalizedRoot, targetDir) = ResolveConfinedPath(slot, relativePath, requireWrite: false, allowRoot: true);
        if (!Directory.Exists(targetDir))
        {
            throw new DirectoryNotFoundException($"Directory '{relativePath}' was not found inside slot '{slot}'.");
        }

        var directoryInfo = new DirectoryInfo(targetDir);
        var entries = new List<FolderEntry>();
        foreach (var entry in directoryInfo.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var isDir = entry is DirectoryInfo;
            var size = isDir ? 0L : ((FileInfo)entry).Length;
            var rel = Path.GetRelativePath(normalizedRoot, entry.FullName).Replace('\\', '/');
            entries.Add(new FolderEntry(entry.Name, rel, isDir, size, entry.LastWriteTimeUtc));
        }

        entries.Sort(static (left, right) =>
        {
            var kindCompare = right.IsDirectory.CompareTo(left.IsDirectory);
            return kindCompare != 0 ? kindCompare : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });

        return Task.FromResult(entries.ToArray());
    }

    public async Task<string> ReadTextAsync(string slot, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: false, allowRoot: false);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File '{relativePath}' was not found inside slot '{slot}'.", fullPath);
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    public async Task<byte[]> ReadBytesAsync(string slot, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: false, allowRoot: false);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File '{relativePath}' was not found inside slot '{slot}'.", fullPath);
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken);
    }

    public async Task WriteTextAsync(string slot, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: true, allowRoot: false);
        EnsureParentDirectory(fullPath);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
    }

    public async Task WriteBytesAsync(string slot, string relativePath, byte[] content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: true, allowRoot: false);
        EnsureParentDirectory(fullPath);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
    }

    public Task<bool> ExistsAsync(string slot, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: false, allowRoot: true);
        return Task.FromResult(File.Exists(fullPath) || Directory.Exists(fullPath));
    }

    public Task DeleteAsync(string slot, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, fullPath) = ResolveConfinedPath(slot, relativePath, requireWrite: true, allowRoot: false);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            return Task.CompletedTask;
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
            return Task.CompletedTask;
        }

        throw new FileNotFoundException($"Entry '{relativePath}' was not found inside slot '{slot}'.", fullPath);
    }

    public Task MoveAsync(string slot, string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, sourcePath) = ResolveConfinedPath(slot, sourceRelativePath, requireWrite: true, allowRoot: false);
        var (_, destinationPath) = ResolveConfinedPath(slot, destinationRelativePath, requireWrite: true, allowRoot: false);

        if (File.Exists(sourcePath))
        {
            EnsureParentDirectory(destinationPath);
            File.Move(sourcePath, destinationPath, overwrite: true);
            return Task.CompletedTask;
        }

        if (Directory.Exists(sourcePath))
        {
            EnsureParentDirectory(destinationPath);
            Directory.Move(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        throw new FileNotFoundException($"Source '{sourceRelativePath}' was not found inside slot '{slot}'.", sourcePath);
    }

    public async Task PublishFileChangedAsync(FileChanged change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAdministrativeCaller();
        ArgumentNullException.ThrowIfNull(change);
        await Publish(change);
    }

    private void EnsureAdministrativeCaller()
    {
        if (Context.Caller?.Actor is Actor.App)
        {
            throw new UnauthorizedAccessException("Apps are not permitted to administer Windows folder grants.");
        }
    }

    private (string NormalizedRoot, string FullPath) ResolveConfinedPath(
        string slot,
        string relativePath,
        bool requireWrite,
        bool allowRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        var normalizedSlot = slot.Trim();

        WindowsFolderGrant? grant;
        if (Context.Caller?.Actor is Actor.App appActor)
        {
            if (string.IsNullOrWhiteSpace(appActor.Key))
            {
                throw new UnauthorizedAccessException("App caller identity is missing.");
            }

            grant = Array.Find(
                State.Grants,
                entry => entry.Enabled && MatchesGrant(entry, appActor.Key, normalizedSlot));
        }
        else
        {
            grant = Array.Find(
                State.Grants,
                entry => entry.Enabled && string.Equals(entry.Slot, normalizedSlot, StringComparison.OrdinalIgnoreCase));
        }

        if (grant is null)
        {
            throw new UnauthorizedAccessException($"No active host folder grant covers slot '{normalizedSlot}'.");
        }

        if (requireWrite && grant.Access != FolderAccess.ReadWrite)
        {
            throw new UnauthorizedAccessException($"Folder slot '{normalizedSlot}' is granted read-only.");
        }

        var normalizedRoot = PathConfinement.Normalize(grant.HostPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Granted host folder '{normalizedRoot}' does not exist.");
        }

        try
        {
            var trimmed = (relativePath ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, ".", StringComparison.Ordinal))
            {
                if (!allowRoot)
                {
                    throw new UnauthorizedAccessException("A non-root relative path is required.");
                }

                return (normalizedRoot, PathConfinement.Resolve([normalizedRoot], normalizedRoot));
            }

            ValidateRelativePath(trimmed);
            var osRelative = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = PathConfinement.Resolve([normalizedRoot], osRelative);
            return (normalizedRoot, fullPath);
        }
        catch (ConfinementDenied ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (Path.IsPathRooted(path) ||
            Path.IsPathFullyQualified(path) ||
            path.StartsWith('/') ||
            path.StartsWith('\\') ||
            path.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path '{path}' must be relative to the granted folder slot.");
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal) ||
                string.Equals(segment, ".", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Path traversal segment in '{path}' is not permitted.");
            }
        }
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
            try
            {
                PathConfinement.RequireNoLinks(parent);
            }
            catch (ConfinementDenied ex)
            {
                throw new UnauthorizedAccessException(ex.Message, ex);
            }
        }
    }

    private static bool MatchesGrant(WindowsFolderGrant grant, string appKey, string slot) =>
        string.Equals(grant.AppKey, appKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(grant.Slot, slot, StringComparison.OrdinalIgnoreCase);
}
