namespace IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;

internal static class RepositoryRoot
{
    internal static string? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        { if (File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { return directory.FullName; } }
        return null;
    }
}
