using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Runtime.Orleans;

namespace IntoChat.OS.Modules.Devices.Link;

internal sealed record LinkSecrets(
    Uri Server,
    string CredentialId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    byte[] PrivateKeyPkcs8);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LinkSecrets))]
[JsonSerializable(typeof(DeviceLinkRecord))]
internal sealed partial class DeviceLinkJson : JsonSerializerContext;

// Machine-protected custody for the device's circle tokens and P-256 private key ({IntoChatOS:Home}/devices/link.bin).
internal sealed class DeviceCredentials(IMachineKeyProtector protector, string file)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => file;

    public async Task<LinkSecrets?> Read(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var stored = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            if (stored.Length == 0)
            {
                return null;
            }

            var plain = protector.Unprotect(stored);
            return JsonSerializer.Deserialize(plain, DeviceLinkJson.Default.LinkSecrets);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Write(LinkSecrets secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var plain = JsonSerializer.SerializeToUtf8Bytes(secrets, DeviceLinkJson.Default.LinkSecrets);
            var stored = protector.Protect(plain);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";

            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(stored, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Delete(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
