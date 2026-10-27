using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Testing;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class FilesWorkspaceFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StaleRequestsCannotStartAnyWrite()
    {
        var storage = new InMemoryStorage();
        var file = storage.AddFile(storage.Root, "selected.txt", [1]);
        var context = new FakeNeuronContext(new(new("owner"), "apps.files", "apps/files/workspace"));
        var host = new FilesWorkspaceNeuron(new FakeCapabilityBroker().Register<IStorage>(storage, "documents"));
        NeuronAttachment.Attach(host, context);
        using var caller = Person.OpenOwner(null).AsCaller();
        await Assert.ThrowsAsync<StaleActionException>(() => host.Request("delete", new(), 900, Guid.NewGuid(), Ct));
        Assert.Empty(context.Operations.All);
        Assert.Equal("selected.txt", (await storage.Stat(file, Ct)).Name);
    }

    [Fact]
    public async Task EmptyRequestIdentityCannotStartAnOperation()
    {
        var context = new FakeNeuronContext(new(new("owner"), "apps.files", "apps/files/workspace"));
        var host = new FilesWorkspaceNeuron(new FakeCapabilityBroker());
        NeuronAttachment.Attach(host, context);
        using var caller = Person.OpenOwner(null).AsCaller();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Request("delete", new(), 0, Guid.Empty, Ct));
        Assert.Empty(context.Operations.All);
    }

    [Fact]
    public async Task AWriteUsesTheSavedSelectionAndCannotReplayEvenAfterReceiptPruning()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, storage) = await Ready();
        var original = Assert.Single((await storage.List(storage.Root, new(), Ct)).Items);
        await Send(host, "select", new(Index: 0));
        var revision = await host.Revision();
        var request = Guid.NewGuid();
        await host.Request("delete", new(), revision, request, Ct);
        var operation = Assert.Single(context.Operations.All).Value;
        Assert.Equal(original.Id, Assert.IsType<WritePlan>(operation.Plan).Source);
        var saved = await context.MemoryState.Load<FilesWorkspaceState>();
        Assert.Contains(request, saved!.Handled);
        Assert.Equal(revision + 1, saved.Files.Revision);
        await host.Request("delete", new(), revision, request, Ct);
        Assert.Single(context.Operations.All);
        for (var n = 0; n < 256; n++) { await Send(host, "fail", new()); }
        Assert.DoesNotContain(request, (await context.MemoryState.Load<FilesWorkspaceState>())!.Handled);
        await Assert.ThrowsAsync<StaleActionException>(() => host.Request("delete", new(), revision, request, Ct));
        Assert.Single(context.Operations.All);
    }

    [Fact]
    public async Task RestartPreservesTheCurrentFolderAndHandledWrites()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, storage) = await Ready();
        await Send(host, "select", new(Index: 0));
        var request = Guid.NewGuid();
        var revision = await host.Revision();
        await host.Request("delete", new(), revision, request, Ct);
        var replacement = new FilesWorkspaceNeuron(new FakeCapabilityBroker().Register<IStorage>(storage, "documents"));
        NeuronAttachment.Attach(replacement, context);
        await NeuronAttachment.Activate(replacement, Ct);
        await replacement.Open(Ct);
        Assert.Equal(storage.Root, (await context.MemoryState.Load<FilesWorkspaceState>())!.Files.Folder);
        await replacement.Request("delete", new(), revision, request, Ct);
        Assert.Single(context.Operations.All);
    }

    [Fact]
    public async Task CompletionUpdatesOnlyItsKnownOperationAndInvalidatesOldItemActions()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, _) = await Ready();
        await Send(host, "select", new(Index: 0));
        await Send(host, "delete", new());
        var operation = Assert.Single(context.Operations.All).Key;
        var revision = await host.Revision();
        await host.On(new OperationDone(new("unrelated"), new DigitalBrain.Operations.OperationResult.Succeeded()));
        Assert.Equal(revision, await host.Revision());
        await host.On(new OperationDone(operation, new DigitalBrain.Operations.OperationResult.Succeeded()));
        var state = (await context.MemoryState.Load<FilesWorkspaceState>())!;
        Assert.True(Assert.Single(state.Operations).Done);
        Assert.Equal("Moved to trash", state.Operations[0].Status);
        Assert.True(await host.Revision() > revision);
        await Assert.ThrowsAsync<StaleActionException>(() => host.Request("select", new(Index: 0), revision, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task CompletionAlreadyJournalledBeforeTheFirstSubscriptionIsRecovered()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, _) = await Ready();
        await Send(host, "select", new(Index: 0));
        // FakeOperations' first id is deterministic; its terminal fact is present before Request can subscribe.
        context.History.Append(new(new("owner"), "runtime.operation", "op-1"),
            new OperationDone(new("op-1"), new DigitalBrain.Operations.OperationResult.Succeeded()), context.Time.GetUtcNow());
        await Send(host, "delete", new());
        var state = (await context.MemoryState.Load<FilesWorkspaceState>())!;
        Assert.True(Assert.Single(state.Operations).Done);
        Assert.Equal("Moved to trash", state.Operations[0].Status);
    }

    [Fact]
    public async Task AnEmptyNextPageKeepsTheAlreadyLoadedRowsVisible()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, storage) = await Ready();
        for (var n = 0; n < 40; n++) { storage.AddFile(storage.Root, $"other-{n:D2}.txt", [1]); }
        await Send(host, "storage", new(Index: 0));
        await Send(host, "browse", new(Index: 0));
        var items = (await storage.List(storage.Root, new(PageSize: 100), Ct)).Items;
        await storage.Write(WritePlan.Delete(items[^1].Id), Ct);
        await Send(host, "more", new());
        var state = (await context.MemoryState.Load<FilesWorkspaceState>())!;
        Assert.Equal(40, state.Files.Items.Length);
        Assert.Equal(0, state.PageOffset);
        var view = await context.Proxies.Get<IView>(new(new("owner"), UIVocabulary.ViewType, "apps/files/main")).Read();
        Assert.Equal(40, view!.Root.Children.Single(item => item.Key == "items").Children.Length);
    }

    [Fact]
    public async Task TheTextWriteLimitCountsUtf8BytesBeforeStartingAnOperation()
    {
        using var caller = Person.OpenOwner(null).AsCaller();
        var (host, context, _) = await Ready();
        await Send(host, "write", new(Name: "multibyte.txt", Text: new string('字', 100_000)));
        Assert.Empty(context.Operations.All);
        Assert.Contains("256 KiB", (await context.MemoryState.Load<FilesWorkspaceState>())!.Notice!, StringComparison.Ordinal);
    }

    private static Task Send(FilesWorkspaceNeuron host, string operation, FilesWorkspaceInput input)
        => SendCore(host, operation, input);


    private static async Task SendCore(FilesWorkspaceNeuron host, string operation, FilesWorkspaceInput input)
        => await host.Request(operation, input, await host.Revision(), Guid.NewGuid(), Ct);

    private static async Task<(FilesWorkspaceNeuron Host, FakeNeuronContext Context, InMemoryStorage Storage)> Ready()
    {
        var storage = new InMemoryStorage();
        storage.AddFile(storage.Root, "original.txt", [1]);
        var context = new FakeNeuronContext(new(new("owner"), "apps.files", "apps/files/workspace"));
        foreach (var slot in new[] { "side", "main", "full" }) { context.Proxies.Register<IView>(new(new("owner"), UIVocabulary.ViewType, "apps/files/" + slot), new View()); }
        foreach (var field in new[] { "search", "name", "content" }) { context.Proxies.Register<ITextField>(new(new("owner"), UIVocabulary.TextFieldType, "apps/files/" + field), new Field()); }
        var host = new FilesWorkspaceNeuron(new FakeCapabilityBroker().Register<IStorage>(storage, "documents"));
        NeuronAttachment.Attach(host, context);
        using var caller = Person.OpenOwner(null).AsCaller();
        await host.Open(Ct);
        await SendCore(host, "storage", new(Index: 0));
        await SendCore(host, "browse", new(Index: 0));
        return (host, context, storage);
    }

    private sealed class View : IView
    {
        private UiView? _view;
        public Task Publish(UiView view) { _view = view; return Task.CompletedTask; }
        public Task<UiView?> Read() => Task.FromResult(_view);
        public Task Activate(long generation, string key, string expectedAction, Guid requestId) => throw new NotSupportedException();
    }

    private sealed class Field : ITextField
    {
        private readonly TextFieldState _state = new();
        public Task Configure(string label, string kind, string? submitButton = null) { _state.Label = label; _state.Kind = kind; return Task.CompletedTask; }
        public Task SetValue(string value) { _state.Value = value; return Task.CompletedTask; }
        public async Task<bool> TrySetValue(string value, int expectedVersion) { await SetValue(value); return true; }
        public Task Input(string value) => SetValue(value);
        public Task<TextFieldState> Read() => Task.FromResult(_state);
    }
}
