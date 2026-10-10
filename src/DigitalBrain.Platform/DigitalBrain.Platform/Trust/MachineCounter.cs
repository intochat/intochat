using System.Globalization;

namespace DigitalBrain.Platform.Trust;

// The machine's signed-call counter (R65), persisted in the machine directory: a restarted machine keeps counting
// upwards, whatever its clock says. R75: it counts calls to one trust deployment, so it lives in that deployment's folder.
internal sealed class MachineCounter(Func<string> file)
{
    public MachineCounter(string path) : this(() => path) { }

    private string? _path;

    private string FilePath => _path ??= file();

    private readonly Lock _gate = new();
    private long? _last;

    public long Next()
    {
        lock (_gate)
        {
            var next = (_last ??= Read()) + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, next.ToString(CultureInfo.InvariantCulture));
            File.Move(temp, FilePath, overwrite: true);
            _last = next;
            return next;
        }
    }

    private long Read() => File.Exists(FilePath) ? long.Parse(File.ReadAllText(FilePath).Trim(), CultureInfo.InvariantCulture) : 0;
}
