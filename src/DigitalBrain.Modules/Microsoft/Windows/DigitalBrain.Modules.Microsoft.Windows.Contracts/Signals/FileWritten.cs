using System.ComponentModel;

namespace DigitalBrain.Microsoft.Windows.Signals;

// Published by IFileSystem.Write only: a file changed on disk by anything else signals nothing.
[GenerateSerializer, Alias("windows.file-written")]
[Description("A file was written through this machine's IFileSystem: its full path and the bytes written.")]
public sealed record FileWritten(
    [property: Id(0)] string Path,
    [property: Id(1)] long Length) : Signal;
