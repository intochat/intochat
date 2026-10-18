using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Parts;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Storage;

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
            BrainLog.WriteFlushed(pending, custody.Wrap(brainId, context, key));
            try { File.Move(pending, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(pending); return Task.FromResult(custody.Unwrap(brainId, context, File.ReadAllBytes(path))); }
            return Task.FromResult(key);
        }
    }
}
