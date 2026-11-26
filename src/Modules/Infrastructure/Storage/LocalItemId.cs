using System.Text;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.LocalDisk;

internal interface IItemIdCodec
{
    ItemId Encode(LocalRoot root, string fullPath);

    (LocalRoot Root, string FullPath) Resolve(ItemId id, IReadOnlyList<LocalRoot> roots);
}

/// <summary>An opaque id: <c>ld:</c> plus the base64url of <c>{rootId}:{relative}</c>. Relative paths use <c>/</c>.</summary>
internal static class LocalItemId
{
    internal const string Prefix = "ld:";

    internal static ItemId Of(string rootId, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        var text = rootId + ":" + (relative ?? "").Replace('\\', '/');
        return new ItemId(Prefix + Base64Url(Encoding.UTF8.GetBytes(text)));
    }

    internal static (string RootId, string Relative) Read(ItemId id)
    {
        if (id.Value is not { } value || !value.StartsWith(Prefix, StringComparison.Ordinal)) { throw new NotFound(id); }
        string text;
        try { text = Encoding.UTF8.GetString(Base64UrlDecode(value[Prefix.Length..])); }
        catch (FormatException) { throw new NotFound(id); }
        var colon = text.IndexOf(':');
        if (colon <= 0) { throw new NotFound(id); }
        var rootId = text[..colon];
        var relative = text[(colon + 1)..];
        if (rootId.Length == 0) { throw new NotFound(id); }
        if (Hostile(relative)) { throw new Denied(id); }
        return (rootId, relative);
    }

    internal static bool Hostile(string relative)
    {
        if (relative.Length == 0) { return false; }
        return !SafeRelativePath.TryParse(relative, out _);
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

// Local-disk and brain-workspace item ids: ld:<base64url(rootId:relative)>.
internal sealed class LocalDiskIdCodec : IItemIdCodec
{
    internal static LocalDiskIdCodec Instance { get; } = new();

    public ItemId Encode(LocalRoot root, string fullPath)
        => LocalItemId.Of(root.Id, ConfinedDiskStorage.Relative(root.Path, fullPath));

    public (LocalRoot Root, string FullPath) Resolve(ItemId id, IReadOnlyList<LocalRoot> roots)
    {
        var (rootId, relative) = LocalItemId.Read(id);
        var root = roots.FirstOrDefault(candidate => candidate.Id == rootId) ?? throw new NotFound(id);
        string combined;
        if (relative.Length == 0)
        {
            combined = root.Path;
        }
        else
        {
            if (!SafeRelativePath.TryParse(relative, out var safeRel))
            {
                throw new Denied(id);
            }
            try
            {
                combined = safeRel.ResolveUnder(root.Path);
            }
            catch (InvalidOperationException error)
            {
                throw new Denied(id, error);
            }
        }
        try
        {
            var full = PathConfinement.Resolve([.. roots.Select(candidate => candidate.Path)], combined);
            if (!ConfinedDiskStorage.Under(root.Path, full)) { throw new Denied(id); }
            return (root, full);
        }
        catch (ConfinementDenied error) { throw new Denied(id, error); }
        catch (UnauthorizedAccessException error) { throw new Denied(id, error); }
    }
}

// Windows PC storage item ids: w:<base64url(normalizedFullPath)>, wire-compatible with WindowsStorage.
internal sealed class WindowsPathIdCodec : IItemIdCodec
{
    internal const string Prefix = "w:";
    internal static WindowsPathIdCodec Instance { get; } = new();

    internal static ItemId Of(string fullPath)
        => new(Prefix + LocalItemId.Base64Url(Encoding.UTF8.GetBytes(fullPath)));

    public ItemId Encode(LocalRoot root, string fullPath)
        => Of(PathConfinement.Normalize(fullPath));

    public (LocalRoot Root, string FullPath) Resolve(ItemId id, IReadOnlyList<LocalRoot> roots)
    {
        if (id.Value is not { } value || !value.StartsWith(Prefix, StringComparison.Ordinal)) { throw new NotFound(id); }
        string path;
        try { path = Encoding.UTF8.GetString(LocalItemId.Base64UrlDecode(value[Prefix.Length..])); }
        catch (FormatException) { throw new NotFound(id); }
        if (string.IsNullOrWhiteSpace(path)) { throw new NotFound(id); }
        try
        {
            var full = PathConfinement.Resolve([.. roots.Select(candidate => candidate.Path)], path);
            var root = ConfinedDiskStorage.RootContaining(roots, full);
            return (root, full);
        }
        catch (ConfinementDenied error) { throw new Denied(id, error); }
        catch (UnauthorizedAccessException error) { throw new Denied(id, error); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw new Denied(id, error); }
    }
}
