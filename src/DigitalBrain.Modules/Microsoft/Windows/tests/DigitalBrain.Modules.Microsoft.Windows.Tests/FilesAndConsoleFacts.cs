using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

public sealed class FilesAndConsoleFacts
{
    private static async Task<(ModuleBrain Host, string Key, string Root)> ConnectedAsync(CancellationToken ct)
    {
        var (host, machineId, root) = await WindowsHost.StartAsync(ct);
        var key = WindowsHost.Machine(machineId);
        await host.Get<IConnection>(key).Connect("device", machineId);
        await host.Get<IWindows>(key).AllowRoot(root);
        return (host, key, root);
    }

    [Fact(Timeout = 60_000)]
    public async Task FilesAreWrittenAndReadInsideARoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, root) = await ConnectedAsync(ct);
        await using var _ = host;
        var files = host.Get<IFileSystem>(WindowsKeys.Files(key));

        await files.Write(@"notes\a.txt", "hi there", ct);

        Assert.Equal("hi there", await files.Read(Path.Combine(root, "notes", "a.txt"), ct));
        Assert.Contains(await files.List("notes", ct), entry => entry.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    public async Task APathOutsideTheRootsIsRefused(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, _) = await ConnectedAsync(ct);
        await using var _ = host;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<IFileSystem>(WindowsKeys.Files(key)).Read(path, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASiblingFolderSharingTheRootsPrefixIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, root) = await ConnectedAsync(ct);
        await using var _ = host;
        var sibling = Directory.CreateDirectory(root + "2").FullName;
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "no");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<IFileSystem>(WindowsKeys.Files(key)).Read(Path.Combine(sibling, "x.txt"), ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ADriveRootAllowsEverythingOnThatDrive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, machineId, root) = await WindowsHost.StartAsync(ct);
        await using var _ = host;
        var key = WindowsHost.Machine(machineId);
        await host.Get<IConnection>(key).Connect("device", machineId);
        var drive = Path.GetPathRoot(root)!;
        File.WriteAllText(Path.Combine(root, "d.txt"), "drive");

        await host.Get<IWindows>(key).AllowRoot(drive);

        Assert.Equal([drive], await host.Get<IWindows>(key).Roots(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("drive", await host.Get<IFileSystem>(WindowsKeys.Files(key)).Read(Path.Combine(root, "d.txt"), ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task RevokingTheRootRefusesTheNextCall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, root) = await ConnectedAsync(ct);
        await using var _ = host;
        var files = host.Get<IFileSystem>(WindowsKeys.Files(key));
        await files.Write("a.txt", "x", ct);

        await host.Get<IWindows>(key).RevokeRoot(root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.Read("a.txt", ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACommandRunsInTheFirstRootByDefault()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, root) = await ConnectedAsync(ct);
        await using var _ = host;

        var result = await host.Get<IConsole>(WindowsKeys.Console(key)).Run("cd", null, 30, ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWorkingDirectoryOutsideTheRootsIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, key, _) = await ConnectedAsync(ct);
        await using var _ = host;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<IConsole>(WindowsKeys.Console(key)).Run("dir", @"C:\", 30, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnotherMachinesFilesAreNotServedHere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await WindowsHost.StartAsync(ct);
        await using var _ = host;
        var other = WindowsHost.Machine(Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => host.Get<IFileSystem>(WindowsKeys.Files(other)).List(".", ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => host.Get<IConsole>(WindowsKeys.Console(other)).Run("cd", null, 30, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task WithoutRootsNothingIsAllowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, machineId, _) = await WindowsHost.StartAsync(ct);
        await using var _ = host;
        var key = WindowsHost.Machine(machineId);
        await host.Get<IConnection>(key).Connect("device", machineId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<IFileSystem>(WindowsKeys.Files(key)).List(".", ct));
    }
}
