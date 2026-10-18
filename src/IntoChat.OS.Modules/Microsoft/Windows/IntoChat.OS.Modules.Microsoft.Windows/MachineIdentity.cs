using Microsoft.Extensions.Options;

namespace IntoChat.OS.Microsoft.Windows;

internal interface IMachineIdentity
{
    string Id { get; }
}

// The install's stable id: created once, then read. A garbled file is an error, never silently replaced,
// because replacing it would orphan the machine's connection.
internal sealed class MachineIdentity(IOptions<WindowsOptions> options) : IMachineIdentity
{
    private readonly Lazy<string> _id = new(() => ReadOrCreate(Resolve(options.Value.MachineIdFile)));

    public string Id => _id.Value;

    private static string Resolve(string configured) => string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "machine-id")
        : configured;

    private static string ReadOrCreate(string file)
    {
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            if (!Guid.TryParseExact(text, "N", out _)) { throw new InvalidDataException($"'{file}' does not hold a machine id."); }
            return text;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(file, id);
        return id;
    }
}
