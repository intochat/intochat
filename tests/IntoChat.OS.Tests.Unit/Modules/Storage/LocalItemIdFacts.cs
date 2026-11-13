using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;

namespace IntoChat.OS.Modules.LocalDisk.Tests.Unit;

public sealed class LocalItemIdFacts
{
    [Fact]
    public void RoundTripKeepsRootAndRelative()
    {
        var id = LocalItemId.Of("root1", "Notes/a.txt");
        var (root, relative) = LocalItemId.Read(id);
        Assert.Equal("root1", root);
        Assert.Equal("Notes/a.txt", relative);
        Assert.StartsWith("ld:", id.Value);
        Assert.DoesNotContain("Notes", id.Value);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../b")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("folder//file")]
    [InlineData("folder\0file")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("sub/../../escape")]
    public void HostileRelativeIsDenied(string relative)
    {
        var id = LocalItemId.Of("root1", relative);
        var error = Assert.Throws<Denied>(() => LocalItemId.Read(id));
        Assert.Equal("denied", error.Code);
    }

    [Fact]
    public void ForeignPrefixIsNotFound()
    {
        var error = Assert.Throws<NotFound>(() => LocalItemId.Read(new ItemId("nope")));
        Assert.Equal("not-found", error.Code);
    }
}

public sealed class LocalDiskRootFacts
{
    [Fact]
    public void SyntaxRejectsBlankAndRelativePaths()
    {
        Assert.Equal(LocalDiskRoots.NeedFullPath, LocalDiskRoots.Syntax(null));
        Assert.Equal(LocalDiskRoots.NeedFullPath, LocalDiskRoots.Syntax("  "));
        Assert.Equal(LocalDiskRoots.NeedFullPath, LocalDiskRoots.Syntax("notes"));
        Assert.Equal(LocalDiskRoots.NeedFullPath, LocalDiskRoots.Syntax("folder\0path"));
        Assert.Null(LocalDiskRoots.Syntax(Path.GetTempPath()));
    }

    [Fact]
    public void PrepareAcceptsARealFolderAndRefusesWindows()
    {
        var folder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("localdisk-root").FullName);
        Assert.Equal(folder, LocalDiskRoots.Prepare(folder));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0)
        {
            var error = Assert.Throws<ConfinementDenied>(() => LocalDiskRoots.Prepare(windows));
            Assert.Equal(LocalDiskRoots.Unavailable, LocalDiskRoots.FailureText(error));
            Assert.NotEqual(LocalDiskRoots.Unavailable, error.Message);
        }
    }

    [Fact]
    public async Task OpenWriteRefusesWritesExceedingMaxWriteBytes()
    {
        var folder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("localdisk-quota").FullName);
        var rootId = Guid.NewGuid().ToString("N");
        var storage = new LocalDiskStorage([new LocalRoot(rootId, folder)]);
        var targetFolder = LocalItemId.Of(rootId, "");
        var plan = new WritePlan(WriteKind.WriteFile, Source: null, TargetFolder: targetFolder, Name: "huge.bin", Conflict: ConflictPolicy.Overwrite);

        Assert.Equal(64L * 1024 * 1024, LocalDiskStorage.MaxWriteBytes);
        await using var writer = await storage.OpenWrite(plan, TestContext.Current.CancellationToken);
        var chunk = new byte[1024 * 1024];
        for (var i = 0; i < 64; i++)
        {
            await writer.Content.WriteAsync(chunk, TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<QuotaExceeded>(async () => await writer.Content.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));
        await writer.Abort(TestContext.Current.CancellationToken);
    }
}

public sealed class LocalDiskModuleOnTestOS : DigitalBrain.Testing.ModuleOnTestOSFacts<LocalDiskModule>;
