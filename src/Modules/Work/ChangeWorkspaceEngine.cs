using DigitalBrain.Log;

namespace IntoChat.OS.Modules.Work;

/// <summary>
/// Workspace engine managing isolated change environments under Path.GetTempPath().
/// NEVER executes or relies on git worktree commands.
/// </summary>
public sealed class ChangeWorkspaceEngine : IDisposable
{
    public string RunId { get; }
    public string TempDirectory { get; }
    public bool IsDisposed { get; private set; }

    public ChangeWorkspaceEngine(string? runId = null)
    {
        RunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N")[..8] : runId.Trim();
        TempDirectory = Path.Combine(Path.GetTempPath(), "intochat-cf-" + RunId);
        Directory.CreateDirectory(TempDirectory);
    }

    public void CloneFrom(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        EnsureNotDisposed();

        foreach (var (relPath, content) in files)
        {
            var target = Path.Combine(TempDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(target, content);
        }
    }

    public void ApplyPatch(IReadOnlyDictionary<string, string> patchFiles)
    {
        ArgumentNullException.ThrowIfNull(patchFiles);
        EnsureNotDisposed();

        foreach (var (relPath, content) in patchFiles)
        {
            var target = Path.Combine(TempDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(target, content);
        }
    }

    public Dictionary<string, string> ReadCurrentFiles()
    {
        EnsureNotDisposed();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(TempDirectory))
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(TempDirectory, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(TempDirectory, file).Replace('\\', '/');
            files[rel] = File.ReadAllText(file);
        }
        return files;
    }

    public StructuredDiff ComputeDiff(IReadOnlyDictionary<string, string> baseFiles)
    {
        EnsureNotDisposed();
        var current = ReadCurrentFiles();
        return UnifiedDiffGenerator.Generate(baseFiles, current);
    }

    public void Cleanup()
    {
        if (Directory.Exists(TempDirectory))
        {
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Best effort ChangeWorkspaceEngine temp cleanup.");
            }
        }
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            Cleanup();
            IsDisposed = true;
        }
    }

    private void EnsureNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ChangeWorkspaceEngine));
        }
    }
}
