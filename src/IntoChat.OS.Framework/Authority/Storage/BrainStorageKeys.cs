using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Brains;
using DigitalBrain.Identity.Local.Custody;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Storage;

// A brain's data key (brain.key in its store directory), wrapped by the master key custody. The runtime BrainLog keeps its
// segments under the same key with the same scheme (moved there in 04-05); the os still seals brain secrets with it.
internal sealed class BrainStorageKeys(IMasterKeyCustody custody, IOptions<BrainStoreOptions> options)
{
    private readonly Lock gate = new();

    internal string DirectoryOf(string brainId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("Invalid brain scope.", nameof(brainId)); }
        return Path.Combine(Path.GetFullPath(options.Value.Directory), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(brainId))).ToLowerInvariant());
    }

    public Task<byte[]> GetOrCreate(string brainId, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            var directory = DirectoryOf(brainId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "brain.key");
            var context = "brain-store-key|" + brainId;
            if (File.Exists(path)) { return Task.FromResult(custody.Unwrap(brainId, context, File.ReadAllBytes(path))); }
            if (Directory.EnumerateFiles(directory).Any(file => Path.GetExtension(file) is ".log" or ".snapshot"))
            { throw new CryptographicException("The brain key is missing; existing state must not be opened with a replacement key."); }
            var key = RandomNumberGenerator.GetBytes(32);
            var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(custody.Wrap(brainId, context, key));
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(pending, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(pending); return Task.FromResult(custody.Unwrap(brainId, context, File.ReadAllBytes(path))); }
            return Task.FromResult(key);
        }
    }
}
