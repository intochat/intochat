using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Platform.Parts;

namespace DigitalBrain.Platform.Trust;

// The pairing secret a machine binds accounts with (R52, R67): the deployment's when configured, otherwise one
// kept in the machine's directory, so every machine is paired at least with the accounts it binds first.
internal static class MachinePairing
{
    public static byte[] HashOf(PartsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PairingSecret is { Length: > 0 } configured) { return SHA256.HashData(Encoding.UTF8.GetBytes(configured)); }
        var path = Path.Combine(options.MachineDirectory, "pairing");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(options.MachineDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            File.Move(temp, path, overwrite: false);
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim()));
    }
}
