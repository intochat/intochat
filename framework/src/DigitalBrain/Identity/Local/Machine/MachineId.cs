using System.Text;

namespace DigitalBrain.Identity.Local.Machine;

/// <summary>
/// The stable id of this machine's install: a GUID in "N" format, created once in a file the host names and read
/// from then on. A file that exists but does not hold an id is an error and is never replaced, because replacing it
/// would orphan everything the old id addresses.
/// </summary>
public sealed class MachineId
{
    private readonly Lazy<string> _value;

    /// <summary>Creates the id kept in <paramref name="file"/>. The file is read, or created, on first use.</summary>
    public MachineId(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var path = Path.GetFullPath(file);
        _value = new(() => ReadOrCreate(path));
    }

    /// <summary>The id.</summary>
    /// <exception cref="InvalidDataException">The file exists but does not hold an id.</exception>
    public string Value => _value.Value;

    private static string ReadOrCreate(string file)
    {
        if (File.Exists(file)) { return Read(file); }
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var id = Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        try
        {
            using var stream = new FileStream(file, options);
            stream.Write(Encoding.UTF8.GetBytes(id));
            return id;
        }
        // Another process created it first; its id is the machine's.
        catch (IOException) when (File.Exists(file)) { return Read(file); }
    }

    private static string Read(string file)
    {
        var text = File.ReadAllText(file).Trim();
        return Guid.TryParseExact(text, "N", out _) ? text : throw new InvalidDataException($"'{file}' does not hold a machine id.");
    }
}
