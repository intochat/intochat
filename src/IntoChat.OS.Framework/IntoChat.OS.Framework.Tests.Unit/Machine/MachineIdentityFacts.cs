using IntoChat.OS.Framework.Machine;

namespace IntoChat.OS.Framework.Tests.Unit.Machine;

public sealed class MachineIdentityFacts : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intochat-machine-id-" + Guid.NewGuid().ToString("N"));

    private string IdFile => Path.Combine(_root, "nested", "machine-id");

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void AMissingFileGetsAGuidInNFormat()
    {
        var id = new FileMachineIdentity(IdFile).Id;

        Assert.True(Guid.TryParseExact(id, "N", out _));
        Assert.Equal(id, File.ReadAllText(IdFile));
        Assert.Equal(Environment.MachineName, new FileMachineIdentity(IdFile).DisplayName);
    }

    [Fact]
    public void TheIdIsStableAcrossConstructions() => Assert.Equal(new FileMachineIdentity(IdFile).Id, new FileMachineIdentity(IdFile).Id);

    [Fact]
    public void AGarbledFileThrowsAndIsNeverReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdFile)!);
        byte[] garbled = [0x6E, 0x6F, 0x74, 0x2D, 0x61, 0x2D, 0x67, 0x75, 0x69, 0x64];
        File.WriteAllBytes(IdFile, garbled);

        Assert.Throws<InvalidDataException>(() => new FileMachineIdentity(IdFile).Id);
        Assert.Equal(garbled, File.ReadAllBytes(IdFile));
    }

    [Fact]
    public void TheIdFileIsOwnerOnlyOnUnix()
    {
        _ = new FileMachineIdentity(IdFile).Id;
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix file mode is not observable on Windows; the 0600 assertion runs on Linux CI."); }
        else { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(IdFile)); }
    }

    [Fact]
    public void TheDefaultPathFollowsTheOperatingSystem()
    {
        var path = MachineIdFile.DefaultPath();

        Assert.Equal("machine-id", Path.GetFileName(path));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "machine-id"), path);
        }
        Assert.Equal("configured", MachineIdFile.Resolve("configured"));
    }
}
