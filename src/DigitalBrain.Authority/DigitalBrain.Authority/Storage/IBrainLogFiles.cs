namespace DigitalBrain.Authority.Storage;

// The journal's file access, so tests can fault an append on any platform.
internal interface IBrainLogFiles
{
    // Appends to the journal; readers may share it.
    Stream OpenAppend(string path);

    // Opens or creates the journal for recovery, which may truncate a torn tail.
    Stream OpenRead(string path);
}

internal sealed class FileBrainLogFiles : IBrainLogFiles
{
    public static readonly FileBrainLogFiles Instance = new();

    public Stream OpenAppend(string path) => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
}
