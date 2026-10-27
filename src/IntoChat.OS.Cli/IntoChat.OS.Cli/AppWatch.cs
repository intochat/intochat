namespace IntoChat.OS.Cli;

/// <summary>One build at a time; every edit invalidates candidates already in progress.</summary>
internal sealed class AppWatch
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private long generation;
    internal Task Change<T>(Func<CancellationToken, Task<T>> build, Func<T, CancellationToken, Task> publish, CancellationToken ct)
        => Change(build, (candidate, _, token) => publish(candidate, token), ct);

    internal async Task Change<T>(Func<CancellationToken, Task<T>> build, Func<T, Func<bool>, CancellationToken, Task> publish, CancellationToken ct)
    {
        var version = Interlocked.Increment(ref generation);
        await serial.WaitAsync(ct);
        try
        {
            if (!Current(version)) { return; }
            var candidate = await build(ct);
            if (!Current(version)) { return; }
            await publish(candidate, () => Current(version), ct);
        }
        finally { serial.Release(); }
    }
    private bool Current(long version) => version == Volatile.Read(ref generation);
    internal void Invalidate() => Interlocked.Increment(ref generation);
    internal static bool Includes(string path) => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part is "bin" or "obj" or ".git" or ".buildcache");
}
