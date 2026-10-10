using System.Globalization;

namespace DigitalBrain.Platform.Trust;

// The machine's signed-call counter (R65), persisted in a file beside its identity key: a restarted machine keeps
// counting upwards, whatever its clock says.
internal sealed class MachineCounter(string path)
{
    private readonly Lock _gate = new();
    private long? _last;

    public long Next()
    {
        lock (_gate)
        {
            var next = (_last ??= Read()) + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, next.ToString(CultureInfo.InvariantCulture));
            File.Move(temp, path, overwrite: true);
            _last = next;
            return next;
        }
    }

    private long Read() => File.Exists(path) ? long.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture) : 0;
}
