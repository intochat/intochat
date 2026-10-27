using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class FilesWorkspaceViewFacts
{
    [Fact]
    [Trait("host", "own")]
    public async Task TheDesktopFullViewContainsBothPublishedWorkspaceViews()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>()
            .ConfigureServices(services => services.AddSingleton<ICapabilityConnections>(new Connections(new InMemoryStorage())))
            .StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        await brain.Get<IFilesWorkspace>(IFilesWorkspace.Key("owner")).Open(ct);
        var full = Assert.IsType<UiView>(await brain.Get<IView>("owner/apps/files/full").Read());
        Assert.Collection(full.Root.Children,
            side => Assert.Equal("files-side", side.Key),
            main => Assert.Equal("files-main", main.Key));
        Assert.Contains(Nodes(full.Root), node => node.Key == "storage-open-0");
        Assert.Contains(Nodes(full.Root), node => node.Key == "items");
        var side = Assert.IsType<UiView>(await brain.Get<IView>("owner/apps/files/side").Read());
        Assert.Contains(Nodes(side.Root), node => node.Key == "storage-open-0");
        var main = Assert.IsType<UiView>(await brain.Get<IView>("owner/apps/files/main").Read());
        Assert.Contains(Nodes(main.Root), node => node.Key == "items");
    }

    [Fact]
    [Trait("host", "own")]
    public async Task AProcessRestartShowsInterruptedWorkWithoutRepeatingTheWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new InMemoryStorage();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICapabilityConnections>(new Connections(storage));
                services.AddSingleton<StorageWriteResult>(async (_, operation, token) =>
                {
                    waiting.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return await storage.Operations.WaitFor(operation, token);
                });
            }).StartAsync(ct);
        try
        {
            using (brain.Owner.AsCaller())
            {
                var workspace = brain.Get<IFilesWorkspace>(IFilesWorkspace.Key("owner"));
                await workspace.Open(ct);
                await workspace.Request("storage", new(Index: 0), await workspace.Revision(), Guid.NewGuid(), ct);
                await workspace.Request("browse", new(Index: 0), await workspace.Revision(), Guid.NewGuid(), ct);
                await workspace.Request("mkdir", new(Name: "before-restart"), await workspace.Revision(), Guid.NewGuid(), ct);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                Assert.Single((await storage.List(storage.Root, new(), ct)).Items);
            }
            await brain.RestartAsync(ct);
            using (brain.Owner.AsCaller())
            {
                await brain.Get<IFilesWorkspace>(IFilesWorkspace.Key("owner")).Open(ct);
                var view = brain.Get<IView>("owner/apps/files/main");
                await Until(async () => Nodes((await view.Read())!.Root).Any(node => node.Key == "progress-0" && node.Text == "Failed: interrupted"), ct);
                Assert.Single((await storage.List(storage.Root, new(), ct)).Items);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    [Trait("host", "own")]
    public async Task AViewActivationCreatesOneFolderAndReactivationRecoversItsCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new InMemoryStorage();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICapabilityConnections>(new Connections(storage));
                services.AddSingleton<StorageWriteResult>(async (_, operation, token) =>
                {
                    waiting.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return await storage.Operations.WaitFor(operation, token);
                });
            }).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var workspace = brain.Get<IFilesWorkspace>(IFilesWorkspace.Key("owner"));
        await workspace.Open(ct);
        await workspace.Request("storage", new(Index: 0), await workspace.Revision(), Guid.NewGuid(), ct);
        await workspace.Request("browse", new(Index: 0), await workspace.Revision(), Guid.NewGuid(), ct);
        await brain.Get<ITextField>("owner/apps/files/name").SetValue("from-view");
        var view = brain.Get<IView>("owner/apps/files/full");
        var before = Assert.IsType<UiView>(await view.Read());
        var button = Assert.Single(Nodes(before.Root), node => node.Key == "mkdir");
        Assert.True(button.Enabled);
        var request = Guid.NewGuid();
        try
        {
            await view.Activate(before.Generation, button.Key, button.Action!, request);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Assert.Equal("from-view", Assert.Single((await storage.List(storage.Root, new(), ct)).Items).Name);
            await Until(async () => Nodes((await view.Read())!.Root).Any(node => node.Key == "cancel-0"), ct);
            // The durable workspace is evicted while its operation is still running. A fresh activation must
            // reconcile completion even if it was journalled before that activation's subscription existed.
            await brain.DeactivateAsync(workspace, ct);
            release.TrySetResult();
            await workspace.Open(ct);
            await Until(async () => Nodes((await view.Read())!.Root).Any(node => node.Key == "progress-0" && node.Text == "Completed"), ct);
            Assert.Single((await storage.List(storage.Root, new(), ct)).Items);
            await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(before.Generation, button.Key, button.Action!, request));
            Assert.Single((await storage.List(storage.Root, new(), ct)).Items);
        }
        finally { release.TrySetResult(); }
    }

    private static IEnumerable<UiNode> Nodes(UiNode node) => new[] { node }.Concat(node.Children.SelectMany(Nodes));

    private static async Task Until(Func<Task<bool>> predicate, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!await predicate()) { await Task.Delay(25, deadline.Token); }
    }

    private sealed class Connections(InMemoryStorage storage) : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BrainConnection>>(brain.Value == "owner"
                ? [new("owner/storage/test", "Documents", true, [Capability.Storage.V1], "m:")] : []);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string key, string callerApp, CancellationToken ct) where TCapability : class
            => brain.Value == "owner" && key == "owner/storage/test"
                ? Task.FromResult((TCapability)(object)storage) : throw new UnauthorizedAccessException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct) => Task.CompletedTask;
    }
}
