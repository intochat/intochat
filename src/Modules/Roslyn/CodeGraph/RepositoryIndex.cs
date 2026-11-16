using System.Collections.Concurrent;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

internal sealed class RepositoryIndex
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (long LastWriteUtcTicks, long Length, CodeGraphService.ParsedSourceFile Parsed)> _files =
        new(StringComparer.Ordinal);
    private CodeGraphService.RepositorySnapshot _snapshot = new([], []);

    public CodeGraphService.RepositorySnapshot Refresh(string repoRoot, CancellationToken cancellationToken)
    {
        var discovered = EnumerateCSharpFiles(repoRoot, cancellationToken);
        lock (_sync)
        {
            var seen = new HashSet<string>(discovered.Count, StringComparer.Ordinal);
            var toParse = new List<(string RelativePath, string FullPath, long Ticks, long Length)>();

            foreach (var item in discovered)
            {
                seen.Add(item.RelativePath);
                if (!_files.TryGetValue(item.RelativePath, out var cached)
                    || cached.LastWriteUtcTicks != item.Ticks
                    || cached.Length != item.Length)
                {
                    toParse.Add(item);
                }
            }

            var removed = _files.Keys.Where(key => !seen.Contains(key)).ToArray();
            if (toParse.Count == 0 && removed.Length == 0)
            {
                return _snapshot;
            }

            foreach (var gone in removed)
            {
                _files.Remove(gone);
            }

            if (toParse.Count > 0)
            {
                var parsedBatch = new ConcurrentBag<(string RelativePath, long Ticks, long Length, CodeGraphService.ParsedSourceFile Parsed)>();
                Parallel.ForEach(toParse, new ParallelOptions { CancellationToken = cancellationToken }, file =>
                {
                    var text = File.ReadAllText(file.FullPath);
                    var parsed = CodeGraphService.ParseSourceFile(file.RelativePath, text, cancellationToken);
                    parsedBatch.Add((file.RelativePath, file.Ticks, file.Length, parsed));
                });

                foreach (var entry in parsedBatch)
                {
                    _files[entry.RelativePath] = (entry.Ticks, entry.Length, entry.Parsed);
                }
            }

            var orderedFiles = _files
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => kv.Value.Parsed)
                .ToArray();

            _snapshot = new CodeGraphService.RepositorySnapshot(
                [.. orderedFiles.SelectMany(static f => f.Symbols)],
                [.. orderedFiles.SelectMany(static f => f.Calls)]);
            return _snapshot;
        }
    }

    private static List<(string RelativePath, string FullPath, long Ticks, long Length)> EnumerateCSharpFiles(
        string repoRoot,
        CancellationToken cancellationToken)
    {
        var results = new List<(string RelativePath, string FullPath, long Ticks, long Length)>();
        var stack = new Stack<string>();
        stack.Push(repoRoot);

        while (stack.Count > 0 && results.Count < CodeGraphService.MaxIndexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = stack.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);
                if (!CodeGraphService.SkippedDirectories.Contains(dirName) && !CodeGraphService.SensitiveSegments.Contains(dirName))
                {
                    stack.Push(subDir);
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir, "*.cs");
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var filePath in files)
            {
                if (results.Count >= CodeGraphService.MaxIndexedFiles)
                {
                    break;
                }

                if (IsGeneratedFileName(filePath))
                {
                    continue;
                }

                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length > CodeGraphService.MaxFileSizeBytes)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
                results.Add((relative, filePath, info.LastWriteTimeUtc.Ticks, info.Length));
            }
        }

        return results;
    }

    private static bool IsGeneratedFileName(string path)
        => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
}
