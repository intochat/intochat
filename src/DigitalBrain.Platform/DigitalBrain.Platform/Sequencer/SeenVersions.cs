using System.Text.Json;

namespace DigitalBrain.Platform.Sequencer;

// The highest head version this machine has seen per part, kept on the machine (Q10): the sequencer can
// withhold new heads but cannot serve an old one unnoticed.
internal sealed class SeenVersions(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, long>? _seen;

    public void Check(string partHash, long version)
    {
        lock (_gate)
        {
            if (Seen().GetValueOrDefault(partHash) > version)
            { throw new RollbackDetectedException($"Part {partHash} was served version {version} after version {Seen()[partHash]}."); }
        }
    }

    public void Saw(string partHash, long version)
    {
        lock (_gate)
        {
            var seen = Seen();
            if (seen.GetValueOrDefault(partHash) >= version) { return; }
            seen[partHash] = version;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(seen));
            File.Move(temp, path, overwrite: true);
        }
    }

    private Dictionary<string, long> Seen() =>
        _seen ??= File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [] : [];
}
