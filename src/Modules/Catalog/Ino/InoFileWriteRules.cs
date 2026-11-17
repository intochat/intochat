using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Apps;

// The rules an Ino file write must meet before the storage is asked. Path confinement and the owner's approval stay with the
// storage itself; these only keep the request to "one new file in the root".
internal static class InoFileWriteRules
{
    internal const int MaxContentCharacters = 1_000_000;

    // The first broken rule, explained for the model; null when the request is acceptable.
    internal static string? Check(string? name, string? content)
    {
        if (string.IsNullOrWhiteSpace(name)) { return "Give a file name, for example hello.txt."; }
        if (!SafeRelativePath.TryParse(name, out var path) || path.Value != name || name.Contains('/', StringComparison.Ordinal))
        { return $"'{name}' must be a file name only, without folders."; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { return $"'{name}' contains characters a file name cannot have."; }
        if ((content?.Length ?? 0) > MaxContentCharacters) { return $"The content is longer than {MaxContentCharacters:N0} characters."; }
        return null;
    }

    // The folder a new file goes to: the owner's Documents root when it is allowed, else the first writable root. Older brains
    // still carry the kernel's working folder as their first root, so "first writable" alone is not enough.
    internal static Item? Target(IReadOnlyList<Item> roots, string? documents = null)
    {
        var writable = roots.Where(item => item.Flags.HasFlag(ItemFlags.Write)).ToArray();
        var name = Path.GetFileName((documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)).TrimEnd('\\', '/'));
        return writable.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) ?? writable.FirstOrDefault();
    }
}
