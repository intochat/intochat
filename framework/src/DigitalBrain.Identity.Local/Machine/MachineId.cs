using System.Text;

namespace DigitalBrain.Identity.Local.Machine;

/// <summary>
/// The stable id of this machine's install: a GUID in "N" format, created once in a file the host names and read
/// from then on. A file that exists but does not hold an id is an error and is never replaced, because replacing it
/// would orphan everything the old id addresses.
/// </summary>
public sealed class MachineId
{
    private const int ReadMaxAttempts = 25;
    private const int InvalidContentMaxAttempts = 8;

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
        if (Path.GetDirectoryName(file) is { Length: > 0 } directory)
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        var id = Guid.NewGuid().ToString("N");
        // On Windows, FileShare.None blocks concurrent readers until the 32-byte write finishes and closes; they retry.
        // On Unix, FileMode.CreateNew (O_CREAT|O_EXCL) is atomic on its own, and FileShare.ReadWrite avoids an advisory
        // flock race after open() that could otherwise fail and leave an empty file behind.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite,
        };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        try
        {
            using var stream = new FileStream(file, options);
            stream.Write(Encoding.UTF8.GetBytes(id));
            return id;
        }
        // Another thread or process created it first; its id is the machine's.
        catch (IOException) when (File.Exists(file)) { return Read(file); }
    }

    private static string Read(string file)
    {
        // A concurrent creator may still hold the file open (sharing violation) or be finishing its first write.
        var invalidAttempts = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var text = File.ReadAllText(file).Trim();
                if (Guid.TryParseExact(text, "N", out _)) { return text; }
                invalidAttempts++;
                if (invalidAttempts >= InvalidContentMaxAttempts || attempt >= ReadMaxAttempts)
                {
                    throw new InvalidDataException($"'{file}' does not hold a machine id.");
                }
            }
            catch (IOException) when (attempt < ReadMaxAttempts)
            {
            }

            Thread.Sleep(Math.Min(5 * attempt, 50));
        }
    }
}
