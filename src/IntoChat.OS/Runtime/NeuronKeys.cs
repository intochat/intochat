// os-only: canonical "{brain}/{key}" neuron key formatting for IntoChat OS modules
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;

namespace IntoChat.OS;

public static class NeuronKeys
{
    public static string FullKey(this NeuronId id) => $"{id.Brain.Value}/{id.Key}";

    public static string SystemShard(string key)
        => "system-" + (SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] & 0x0F).ToString("X2");
}
