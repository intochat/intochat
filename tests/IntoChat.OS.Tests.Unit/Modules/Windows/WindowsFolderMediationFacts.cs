using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using IntoChat.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

public sealed class WindowsFolderMediationFacts : IDisposable
{
    private static readonly BrainId Owner = new("owner");
    private static readonly NeuronId GatewayId = IWindowsFolderGateway.Key(Owner.Value);

    private readonly string _sandboxRoot = Path.Combine(
        Path.GetTempPath(),
        "intochat-windows-folder-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _grantDir;
    private readonly string _outsideFile;

    public WindowsFolderMediationFacts()
    {
        _grantDir = Path.Combine(_sandboxRoot, "granted");
        _outsideFile = Path.Combine(_sandboxRoot, "secret.txt");
        Directory.CreateDirectory(_grantDir);
        File.WriteAllText(_outsideFile, "top-secret");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_sandboxRoot))
        {
            Directory.Delete(_sandboxRoot, recursive: true);
        }
    }

    private TestOS CreateOs(WindowsFolderWatcher? watcher = null)
    {
        var services = new ServiceCollection();
        services.Configure<WindowsOptions>(options =>
        {
            options.MachineIdFile = Path.Combine(_sandboxRoot, "machine-id");
            options.AutoEnrollThisPc = false;
        });
        services.AddSingleton<MachineIdentity>();
        services.AddSingleton<WindowsStorageCoordinator>();
        services.AddSingleton(watcher ?? new WindowsFolderWatcher());

        return TestOS.Local(options => options.Services = services.BuildServiceProvider())
            .Register(typeof(WindowsModule).Assembly);
    }

    [Fact]
    public async Task GrantedReadWriteAppCanPerformScopedCrudAndMove()
    {
        await using var os = CreateOs();
        var gateway = os.Get<IWindowsFolderGateway>(GatewayId);

        await gateway.GrantSlotAsync(
            new WindowsFolderGrant("app-a", "downloads", _grantDir, FolderAccess.ReadWrite),
            Ct);

        using (os.Edge.Enter(os.Edge.App("app-a", new Actor.Person("p1", "owner"), Owner)))
        {
            await gateway.WriteTextAsync("downloads", "sub/invoice.txt", "amount=42", Ct);
            await gateway.WriteBytesAsync("downloads", "raw.bin", [1, 2, 3, 4], Ct);

            Assert.True(await gateway.ExistsAsync("downloads", "sub/invoice.txt", Ct));
            Assert.Equal("amount=42", await gateway.ReadTextAsync("downloads", "sub/invoice.txt", Ct));
            Assert.Equal([1, 2, 3, 4], await gateway.ReadBytesAsync("downloads", "raw.bin", Ct));

            var entries = await gateway.ListAsync("downloads", "", Ct);
            Assert.Contains(entries, entry => entry.Name == "sub" && entry.IsDirectory && entry.RelativePath == "sub");
            Assert.Contains(entries, entry => entry.Name == "raw.bin" && !entry.IsDirectory && entry.SizeBytes == 4);

            await gateway.MoveAsync("downloads", "sub/invoice.txt", "Processed/invoice.txt", Ct);
            Assert.False(await gateway.ExistsAsync("downloads", "sub/invoice.txt", Ct));
            Assert.True(await gateway.ExistsAsync("downloads", "Processed/invoice.txt", Ct));
            Assert.Equal("amount=42", await gateway.ReadTextAsync("downloads", "Processed/invoice.txt", Ct));

            await gateway.DeleteAsync("downloads", "Processed/invoice.txt", Ct);
            Assert.False(await gateway.ExistsAsync("downloads", "Processed/invoice.txt", Ct));
        }
    }

    [Fact]
    public async Task PathTraversalAndRootedPathsAreRejected()
    {
        await using var os = CreateOs();
        var gateway = os.Get<IWindowsFolderGateway>(GatewayId);

        await File.WriteAllTextAsync(Path.Combine(_grantDir, "ok.txt"), "inside", Ct);
        await gateway.GrantSlotAsync(
            new WindowsFolderGrant("app-a", "downloads", _grantDir, FolderAccess.ReadWrite),
            Ct);

        using (os.Edge.Enter(os.Edge.App("app-a", new Actor.Person("p1", "owner"), Owner)))
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.ReadTextAsync("downloads", @"..\secret.txt", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.ReadTextAsync("downloads", "../secret.txt", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.ReadTextAsync("downloads", @"C:\Windows\System32\drivers\etc\hosts", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.ExistsAsync("downloads", @"..\secret.txt", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.WriteTextAsync("downloads", @"..\escape.txt", "bad", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.MoveAsync("downloads", "ok.txt", @"..\escape.txt", Ct));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
                gateway.DeleteAsync("downloads", "", Ct));
        }
    }

    [Fact]
    public async Task ReadOnlyGrantAllowsReadsAndRefusesMutations()
    {
        await using var os = CreateOs();
        var gateway = os.Get<IWindowsFolderGateway>(GatewayId);

        await File.WriteAllTextAsync(Path.Combine(_grantDir, "readme.txt"), "hello", Ct);
        await gateway.GrantSlotAsync(
            new WindowsFolderGrant("app-a", "downloads", _grantDir, FolderAccess.Read),
            Ct);

        using (os.Edge.Enter(os.Edge.App("app-a", new Actor.Person("p1", "owner"), Owner)))
        {
            Assert.True(await gateway.ExistsAsync("downloads", "readme.txt", Ct));
            Assert.Equal("hello", await gateway.ReadTextAsync("downloads", "readme.txt", Ct));
            Assert.Single(await gateway.ListAsync("downloads", "", Ct));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.WriteTextAsync("downloads", "new.txt", "blocked", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.WriteBytesAsync("downloads", "new.bin", [1], Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.DeleteAsync("downloads", "readme.txt", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.MoveAsync("downloads", "readme.txt", "moved.txt", Ct));
        }
    }

    [Fact]
    public async Task CrossAppIsolationAndAdminEndpointsAreEnforced()
    {
        await using var os = CreateOs();
        var gateway = os.Get<IWindowsFolderGateway>(GatewayId);

        await gateway.GrantSlotAsync(
            new WindowsFolderGrant("app-b", "downloads", _grantDir, FolderAccess.ReadWrite),
            Ct);

        using (os.Edge.Enter(os.Edge.App("app-a", new Actor.Person("p1", "owner"), Owner)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.ListAsync("downloads", "", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.GrantSlotAsync(
                    new WindowsFolderGrant("app-a", "downloads", _grantDir, FolderAccess.ReadWrite),
                    Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.RevokeAppAsync("app-b", Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.ListGrantsAsync(Ct));
        }

        var grants = await gateway.ListGrantsAsync(Ct);
        Assert.Single(grants);
        Assert.Equal("app-b", grants[0].AppKey);

        await gateway.RevokeAppAsync("app-b", Ct);
        Assert.Empty(await gateway.ListGrantsAsync(Ct));

        using (os.Edge.Enter(os.Edge.App("app-b", new Actor.Person("p1", "owner"), Owner)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                gateway.ListAsync("downloads", "", Ct));
        }
    }

    [Fact]
    public async Task WatcherDebouncesFileChangesIntoFileChangedSignal()
    {
        var delivered = new TaskCompletionSource<(string BrainId, FileChanged Signal)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        using var watcher = new WindowsFolderWatcher(
            services: null,
            publisher: (brainId, signal, _) =>
            {
                Interlocked.Increment(ref count);
                delivered.TrySetResult((brainId, signal));
                return Task.CompletedTask;
            },
            debounce: TimeSpan.FromMilliseconds(80));

        await watcher.StartAsync(Ct);
        try
        {
            var subDir = Path.Combine(_grantDir, "sub");
            Directory.CreateDirectory(subDir);

            watcher.SyncBrain(
                Owner.Value,
                [new WindowsFolderGrant("app-a", "downloads", _grantDir, FolderAccess.ReadWrite)]);

            var targetFile = Path.Combine(subDir, "drop.pdf");

            await File.WriteAllTextAsync(targetFile, "part1-", Ct);
            await File.AppendAllTextAsync(targetFile, "part2", Ct);

            var (brainId, signal) = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(Owner.Value, brainId);
            Assert.Equal("downloads", signal.Slot);
            Assert.Equal("sub/drop.pdf", signal.RelativePath);
            Assert.True(signal.SizeBytes > 0);
            Assert.True(signal.Kind is "Created" or "Changed");

            // Give one more debounce window to verify rapid bursts collapsed into a single signal.
            await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);
            Assert.Equal(1, Volatile.Read(ref count));
        }
        finally
        {
            await watcher.StopAsync(Ct);
        }
    }
}
