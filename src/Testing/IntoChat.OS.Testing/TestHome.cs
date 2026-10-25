using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

// A temporary IntoChatOS:Home for one test host: a unique directory under the OS temp directory, deleted on dispose.
// Test hosts never reach the developer's ~/.intochat.
public sealed class TestHome : IDisposable, IAsyncDisposable
{
    private TestHome(string path) => Path = path;

    public string Path { get; }
    public string State => System.IO.Path.Combine(Path, StateHome.StateDirectoryName);
    public KeyValuePair<string, string?> Setting => new(IntoChatOSNames.HomeKey, Path);

    public static TestHome Create()
        => new(Directory.CreateTempSubdirectory("intochat-home-").FullName);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
