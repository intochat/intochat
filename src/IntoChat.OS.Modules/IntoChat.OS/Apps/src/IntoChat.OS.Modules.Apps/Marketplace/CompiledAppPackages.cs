using System.Security.Cryptography;
using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Modules.Apps;

// The original archive is retained so restart can verify the exact reviewed digest, not just trust cached JSON.
internal sealed class CompiledAppPackages : IDisposable
{
    private readonly string _archives;
    private readonly string _payloads;
    private readonly PackageLimits _limits;
    private readonly AppPackageReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal CompiledAppPackages(string root, IReadOnlyDictionary<string, string> sharedAssemblies, PackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(sharedAssemblies);
        _limits = limits ?? new();
        _reader = new(_limits, new Dictionary<string, string>(sharedAssemblies, StringComparer.Ordinal));
        var boundary = Path.GetFullPath(root);
        EnsureDirectory(boundary);
        _archives = Path.Combine(boundary, "archives");
        _payloads = Path.Combine(boundary, "payloads");
        EnsureDirectory(_archives);
        EnsureDirectory(_payloads);
    }

    internal async Task<ValidatedAppPackage> Stage(Stream archive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var temporary = Path.Combine(_archives, ".incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await archive.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    if (count > _limits.MaxArchiveBytes - length) { throw new InvalidDataException("Package stream limit exceeded."); }
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    length += count;
                }
            }
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ValidatedAppPackage package;
                await using (var input = File.OpenRead(temporary))
                { package = await _reader.Read(input, _payloads, ct).ConfigureAwait(false); }
                var destination = ArchivePath(package.Digest);
                if (File.Exists(destination)) { await VerifyArchive(destination, package.Digest, ct).ConfigureAwait(false); }
                else
                {
                    try { File.Move(temporary, destination); }
                    catch (IOException) when (File.Exists(destination))
                    { await VerifyArchive(destination, package.Digest, ct).ConfigureAwait(false); }
                    File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
                }
                return package;
            }
            finally { _gate.Release(); }
        }
        finally { File.Delete(temporary); }
    }

    internal async Task<ValidatedAppPackage> Read(string digest, CancellationToken ct)
    {
        var path = ArchivePath(digest);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyArchive(path, digest, ct).ConfigureAwait(false);
            await using var archive = File.OpenRead(path);
            var package = await _reader.Read(archive, _payloads, ct).ConfigureAwait(false);
            if (package.Digest != digest) { throw new InvalidDataException("The saved package differs from its reviewed digest."); }
            return package;
        }
        finally { _gate.Release(); }
    }

    private string ArchivePath(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigitLower(c)))
        { throw new ArgumentException("A package reference is a lowercase SHA256 value.", nameof(digest)); }
        return Path.Combine(_archives, digest + ".intochat-app");
    }

    private async Task VerifyArchive(string path, string digest, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("A saved package cannot be a link."); }
        await using var archive = File.OpenRead(path);
        if (archive.Length > _limits.MaxArchiveBytes
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, ct).ConfigureAwait(false)) != digest)
        { throw new InvalidDataException("The saved package differs from its reviewed digest."); }
    }

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("The package cache cannot be a link."); }
        Directory.CreateDirectory(path);
    }

    public void Dispose() => _gate.Dispose();
}
