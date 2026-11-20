// os-only: os machine-id file under the IntoChatOS configuration section.
using System.Text;

namespace IntoChat.OS.Framework.Machine;

// The machine-id file: a GUID in "N" format. A garbled file is an error, never silently replaced, because replacing
// it would orphan the machine's enrollment (T-10-07).
public static class MachineIdFile
{
    public const string ConfigurationKey = "IntoChatOS:Machine:IdFile";

    internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Windows keeps %LOCALAPPDATA%\IntoChatOS\machine-id; macOS uses Application Support; Linux and containers use
    // $XDG_DATA_HOME/intochat-os, falling back to ~/.local/share/intochat-os.
    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "machine-id");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) { return Path.Combine(home, "Library", "Application Support", "IntoChatOS", "machine-id"); }
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(home, ".local", "share") : data, "intochat-os", "machine-id");
    }

    public static string Resolve(string? configured) => string.IsNullOrWhiteSpace(configured) ? DefaultPath() : configured;

    public static string ReadOrCreate(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            if (!Guid.TryParseExact(text, "N", out _)) { throw new InvalidDataException($"'{file}' does not hold a machine id."); }
            return text;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var id = Guid.NewGuid().ToString("N");
        WriteOwnerOnly(file, Encoding.UTF8.GetBytes(id));
        return id;
    }

    // Creates the file, failing if it exists, readable and writable by the owner only on Unix.
    internal static void WriteOwnerOnly(string file, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = OwnerOnly; }
        using var stream = new FileStream(file, options);
        stream.Write(content);
    }
}
