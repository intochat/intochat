namespace DigitalBrain.Testing;

/// <summary>
/// A unique temporary directory under <see cref="System.IO.Path.GetTempPath"/> that is deterministically deleted
/// on <see cref="Dispose"/>, clearing read-only attributes and retrying briefly on transient file-lock races.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private const int DefaultMaxAttempts = 10;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(150);

    private static readonly EnumerationOptions ClearAttributesOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>Creates a new unique temporary directory whose name starts with <paramref name="prefix"/>.</summary>
    public TempDirectory(string prefix = "db-test-", bool create = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        if (create)
        {
            Directory.CreateDirectory(Path);
        }
    }

    /// <summary>Full path to the temporary directory.</summary>
    public string Path { get; }

    /// <summary>Deletes the temporary directory if it still exists.</summary>
    public void Dispose() => DeleteIfPresent(Path);

    /// <summary>Returns <see cref="Path"/>.</summary>
    public override string ToString() => Path;

    /// <summary>
    /// Deletes <paramref name="path"/> recursively if it exists, clearing read-only attributes and retrying on
    /// transient <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> locks.
    /// </summary>
    public static void DeleteIfPresent(string? path, int maxAttempts = DefaultMaxAttempts)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts - 1)
            {
                ClearReadOnlyAttributes(path);
                Thread.Sleep(DefaultRetryDelay);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", ClearAttributesOptions))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort attribute clearing before retrying delete.
                }
            }

            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(root, rootAttributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory may already be partially removed or transiently locked.
        }
    }
}
