using System.Text.Json;

namespace DigitalBrain.Platform.Sequencer;

// The highest head version this machine has seen per part, kept on the machine (Q10): the sequencer can
// withhold new heads but cannot serve an old one unnoticed. R75: the memory is of one trust deployment, so its file
// lives in that deployment's folder of the machine directory, resolved on first use.
internal sealed class SeenVersions(Func<string> file)
{
    public SeenVersions(string path) : this(() => path) { }

    private string? _path;

    private string FilePath => _path ??= file();

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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(seen));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private Dictionary<string, long> Seen() =>
        _seen ??= File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(FilePath)) ?? [] : [];
}
