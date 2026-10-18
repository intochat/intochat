using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Microsoft.Windows.Signals;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests.Integration;

public sealed class FilesAndConsoleFacts(WindowsMachineHost shared)
{
    [Fact]
    public async Task FilesAreWrittenAndReadInsideARoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var files = brain.Get<IFileSystem>(WindowsKeys.Files(host.Key));

        await files.Write(@"notes\a.txt", "hi there", ct);

        Assert.Equal("hi there", await files.Read(Path.Combine(host.Root, "notes", "a.txt"), ct));
        Assert.Contains(await files.List("notes", ct), entry => entry.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AWriteInsideARootPublishesFileWritten()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var files = brain.Get<IFileSystem>(WindowsKeys.Files(host.Key));
        await using var written = await brain.Observe<FileWritten>(files, ct);

        await files.Write(@"notes\b.txt", "héllo", ct);

        var signal = await written.NextAsync(ct: ct);
        Assert.Equal(Path.Combine(host.Root, "notes", "b.txt"), signal.Path, ignoreCase: true);
        Assert.Equal(new FileInfo(signal.Path).Length, signal.Length);
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    public async Task APathOutsideTheRootsIsRefused(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IFileSystem>(WindowsKeys.Files(host.Key)).Read(path, ct));
    }

    [Fact]
    public async Task ASiblingFolderSharingTheRootsPrefixIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var sibling = Directory.CreateDirectory(host.Root + "2").FullName;
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "no");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IFileSystem>(WindowsKeys.Files(host.Key)).Read(Path.Combine(sibling, "x.txt"), ct));
    }

    [Fact]
    public async Task ADriveRootAllowsEverythingOnThatDrive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var drive = Path.GetPathRoot(host.Root)!;
        File.WriteAllText(Path.Combine(host.Root, "d.txt"), "drive");

        await brain.Get<IWindows>(host.Key).AllowRoot(drive);

        Assert.Equal([drive], await brain.Get<IWindows>(host.Key).Roots(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("drive", await brain.Get<IFileSystem>(WindowsKeys.Files(host.Key)).Read(Path.Combine(host.Root, "d.txt"), ct));
    }

    [Fact]
    public async Task RevokingTheRootRefusesTheNextCall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var files = brain.Get<IFileSystem>(WindowsKeys.Files(host.Key));
        await files.Write("a.txt", "x", ct);

        await brain.Get<IWindows>(host.Key).RevokeRoot(host.Root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.Read("a.txt", ct));
    }

    [Fact]
    public async Task ACommandRunsInTheFirstRootByDefault()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();

        var result = await brain.Get<IConsole>(WindowsKeys.Console(host.Key)).Run("cd", null, 30, ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetFullPath(host.Root).TrimEnd(Path.DirectorySeparatorChar), result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task AWorkingDirectoryOutsideTheRootsIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IConsole>(WindowsKeys.Console(host.Key)).Run("dir", @"C:\", 30, ct));
    }

    [Fact]
    public async Task AnotherMachinesFilesAreNotServedHere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var other = host.Machine(Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => brain.Get<IFileSystem>(WindowsKeys.Files(other)).List(".", ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => brain.Get<IConsole>(WindowsKeys.Console(other)).Run("cd", null, 30, ct));
    }

    [Fact]
    public async Task WithoutRootsNothingIsAllowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<IFileSystem>(WindowsKeys.Files(host.Key)).List(".", ct));
    }
}
