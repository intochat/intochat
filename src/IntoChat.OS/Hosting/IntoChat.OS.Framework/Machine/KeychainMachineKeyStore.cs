using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace IntoChat.OS.Framework.Machine;

// macOS: the PKCS#8 private key, base64, as a generic-password keychain item under a fixed service name. The
// security CLI runs from an argument list, never a shell string (T-10-08).
[SupportedOSPlatform("macos")]
public sealed class KeychainMachineKeyStore(string account, string service = KeychainMachineKeyStore.DefaultService) : IMachineKeyStore
{
    public const string DefaultService = "intochat-os-machine-key";
    private const string Security = "/usr/bin/security";
    private const int ItemNotFound = 44;

    public string Custody => "keychain";

    public async ValueTask EnsureKeyAsync(CancellationToken cancellationToken)
    {
        if (await ReadAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            CryptographicOperations.ZeroMemory(existing);
            return;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var (code, _) = await RunAsync(["add-generic-password", "-U", "-s", service, "-a", account, "-w", Convert.ToBase64String(pkcs8)], cancellationToken)
                .ConfigureAwait(false);
            if (code != 0) { throw new MachineKeyUnavailableException(); }
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    public async ValueTask<byte[]> PublicKeySpkiAsync(CancellationToken cancellationToken)
    {
        using var key = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return key.ExportSubjectPublicKeyInfo();
    }

    public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var key = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        var (code, _) = await RunAsync(["delete-generic-password", "-s", service, "-a", account], cancellationToken).ConfigureAwait(false);
        if (code is not 0 and not ItemNotFound) { throw new MachineKeyUnavailableException(); }
    }

    private async ValueTask<ECDsa> LoadAsync(CancellationToken cancellationToken)
    {
        var pkcs8 = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? throw new MachineKeyUnavailableException();
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        catch (CryptographicException exception) { throw new MachineKeyUnavailableException(exception); }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    private async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await RunAsync(["find-generic-password", "-s", service, "-a", account, "-w"], cancellationToken).ConfigureAwait(false);
        if (code == ItemNotFound) { return null; }
        if (code != 0) { throw new MachineKeyUnavailableException(); }
        try { return Convert.FromBase64String(output.Trim()); }
        catch (FormatException exception) { throw new MachineKeyUnavailableException(exception); }
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Security)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new MachineKeyUnavailableException();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }
}
