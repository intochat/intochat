using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Files is a screen (app.files/default): its view is the screen's own, and every button's command reaches it through POST /act.
public sealed class FilesWorkspaceScreenFacts
{
    private const string Brain = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Files_is_the_compiled_screen_app_files()
    {
        var compiled = Assert.Single(CompiledApps.Of(typeof(AppsModule).Assembly), app => app.Bundle.Name == "files");
        Assert.Equal(typeof(FilesWorkspaceScreen), compiled.AppType);
        Assert.Equal("app.files", typeof(FilesWorkspaceScreen).GetCustomAttributes(typeof(NeuronAttribute), false).Cast<NeuronAttribute>().Single().Type);
        Assert.Equal(typeof(FilesWorkspaceScreen).AssemblyQualifiedName, compiled.Bundle.CompiledType);
        Assert.Equal(("Files", "folder", 2, "Workspace"), (compiled.Bundle.Manifest.Title, compiled.Bundle.Manifest.Icon, compiled.Bundle.Manifest.Dock, compiled.Bundle.Manifest.Category));
        Assert.Contains("storage@1", compiled.Bundle.Manifest.Requires!.Capabilities ?? []);
        // The views the old host published as ui.view neurons, and the workspace neuron itself, are gone.
        Assert.DoesNotContain(typeof(AppsModule).Assembly.GetTypes(), type => type.GetCustomAttributes(typeof(NeuronAttribute), false).Cast<NeuronAttribute>().Any(n => n.Type == "apps.files"));
    }

    [Fact]
    public async Task Opening_lists_the_storages_and_browse_select_and_preview_go_through_act()
    {
        var (storage, _, _) = Disk();
        await using var os = Os(storage);
        using var person = AsPerson();

        var view = await ViewOf(os);
        Assert.True(view.Contains("\"storage-open-0\"", StringComparison.Ordinal), view);
        Assert.DoesNotContain("\"neuronRef\"", view, StringComparison.Ordinal);

        await Press(os, "storage-open-0");
        await Press(os, Key(await ViewOf(os), "disk"));
        await Press(os, Key(await ViewOf(os), "docs"));
        var inDocs = await ViewOf(os);
        Assert.Contains("note.md", inDocs, StringComparison.Ordinal);

        await Press(os, Key(inDocs, "note.md"));
        var selected = await ViewOf(os);
        Assert.Contains("# Hello", selected, StringComparison.Ordinal);
        Assert.Contains("\"save-edit\"", selected, StringComparison.Ordinal);

        await Press(os, "up");
        Assert.DoesNotContain("note.md", await ViewOf(os), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_mkdir_rename_and_save_edit_change_the_storage_through_act()
    {
        var (storage, docs, note) = Disk();
        await using var os = Os(storage);
        using var person = AsPerson();
        await Press(os, "storage-open-0");
        await Press(os, Key(await ViewOf(os), "disk"));
        await Press(os, Key(await ViewOf(os), "docs"));

        // Write: what the person typed into the two quick-write fields.
        await Type(os, "input-name", "todo.txt");
        await Type(os, "input-content", "buy milk");
        await Press(os, "write");
        Assert.Equal("buy milk", await Text(storage, docs, "todo.txt"));

        // A new folder with no name asks inline, then takes the prompt's text.
        await Type(os, "input-name", "");
        await Press(os, "mkdir");
        Assert.Contains("\"prompt-confirm\"", await ViewOf(os), StringComparison.Ordinal);
        await Type(os, "prompt-input", "reports");
        await Press(os, "prompt-confirm");
        Assert.Contains((await storage.List(docs, new ListQuery(), Ct)).Items, item => item is { Name: "reports", Kind: ItemKind.Folder });
        Assert.DoesNotContain("\"prompt-confirm\"", await ViewOf(os), StringComparison.Ordinal);

        // Rename the selected file through the prompt, which starts with its current name.
        await Press(os, "up");
        await Press(os, Key(await ViewOf(os), "docs"));
        await Press(os, Key(await ViewOf(os), "note.md"));
        await Press(os, "rename");
        Assert.Equal("note.md", Shown(await ViewOf(os), "prompt-input"));
        await Type(os, "prompt-input", "readme.md");
        await Press(os, "prompt-confirm");
        Assert.Equal("readme.md", (await storage.Stat(note, Ct)).Name);

        // Save an edit of the selected text file: it replaces the file, which the person approves.
        os.Confirmations.ApproveAll();
        await Press(os, "up");
        await Press(os, Key(await ViewOf(os), "docs"));
        await Press(os, Key(await ViewOf(os), "todo.txt"));
        Assert.Equal("buy milk", Shown(await ViewOf(os), "editor-content"));
        await Type(os, "editor-content", "buy oat milk");
        await Press(os, "save-edit");
        Assert.Equal("buy oat milk", await Text(storage, docs, "todo.txt"));
    }

    [Fact]
    public async Task Typing_into_search_and_filter_runs_them_through_act()
    {
        var (storage, _, _) = Disk();
        await using var os = Os(storage);
        using var person = AsPerson();
        await Press(os, "storage-open-0");
        await Press(os, Key(await ViewOf(os), "disk"));
        await Press(os, Key(await ViewOf(os), "docs"));

        await Type(os, "filter", "zzz");
        Assert.DoesNotContain("note.md", await ViewOf(os), StringComparison.Ordinal);
        await Type(os, "filter", "");
        Assert.Contains("note.md", await ViewOf(os), StringComparison.Ordinal);

        await Type(os, "search", "note");
        var found = await ViewOf(os);
        Assert.Contains("Search results for", found, StringComparison.Ordinal);
        Assert.Contains("note.md", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_drawn_at_an_older_revision_is_refused_and_changes_nothing()
    {
        var (storage, _, _) = Disk();
        await using var os = Os(storage);
        using var person = AsPerson();
        var old = await ViewOf(os);
        await Press(os, "storage-open-0");

        var stale = Command(old, "storage-open-0");
        var result = await AppUiEndpoints.Act(new($"{Brain}/apps/files", stale.Target, stale.Signal, stale.Args), new Proxies(os), Brain, Ct, Caller());

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("Files changed", JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_of_another_brain_is_refused()
    {
        var (storage, _, _) = Disk();
        await using var os = Os(storage);
        string view;
        using (AsPerson()) { view = await ViewOf(os); }
        var command = Command(view, "storage-open-0");
        using var stranger = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("mallory", "mallory", new BrainId("mallory")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Screen(os).Dispatch(command));
    }

    [Fact]
    public async Task Quick_write_panel_toggles_through_screen_state_without_legacy_expander()
    {
        var (storage, _, _) = Disk();
        await using var os = Os(storage);
        using var person = AsPerson();

        var initial = await ViewOf(os);
        Assert.DoesNotContain("expander:", initial, StringComparison.Ordinal);
        Assert.Contains("\"input-name\"", initial, StringComparison.Ordinal);

        await Press(os, "toggle-quick-write");
        var collapsed = await ViewOf(os);
        Assert.DoesNotContain("\"input-name\"", collapsed, StringComparison.Ordinal);

        await Press(os, "toggle-quick-write");
        var reopened = await ViewOf(os);
        Assert.Contains("\"input-name\"", reopened, StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_cards_show_what_the_operation_signals_reported()
    {
        var cards = FilesWorkspaceScreen.OperationCards(
            [
                new("op-1", "write"),
                new("op-2", "Move to trash", Done: true, Fraction: 1, Status: FilesWorkspaceScreen.StatusOf(new("op-2", "Move to trash"), new OperationResult.Succeeded())),
                new("op-3", "rename", Done: true, Status: FilesWorkspaceScreen.StatusOf(new("op-3", "rename"), new OperationResult.Failed("conflict", "exists")), Failed: true),
            ],
            index => FilesUi.Button("cancel-" + index, "Cancel", FilesUi.Act("1", "cancel", index)));
        var wire = string.Join("\n", cards.Select(card => JsonSerializer.Serialize(card, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(Node)))));

        Assert.Contains("cancel-0", wire, StringComparison.Ordinal);
        Assert.Contains("Moved to trash", wire, StringComparison.Ordinal);
        Assert.Contains("Failed: conflict", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("cancel-1", wire, StringComparison.Ordinal);
    }

    private static (InMemoryStorage Storage, ItemId Docs, ItemId Note) Disk()
    {
        var storage = new InMemoryStorage("disk", kind: "local");
        var docs = storage.AddFolder(storage.Root, "docs");
        var note = storage.AddFile(docs, "note.md", "# Hello"u8.ToArray());
        return (storage, docs, note);
    }

    private static TestOS Os(InMemoryStorage storage)
    {
        var os = TestOS.Local(options => options.Services = new ServiceCollection().AddLogging().BuildServiceProvider());
        os.With<IStorage>("disk", storage);
        os.Register<FilesWorkspaceScreen>();
        os.Register(typeof(FlutterModule).Assembly);
        return os;
    }

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId(Brain)));

    private static Caller? Caller() => IntoChat.OS.Tests.TestEdge.Authority.Current;

    private static IScreen Screen(TestOS os) => os.Get<IScreen>(new NeuronId(new BrainId(Brain), "app.files", "default"));

    // Types into the field keyed <paramref name="key"/> as the client does: after a pause, the field's command with the typed text as
    // its value, posted to /act.
    private static async Task Type(TestOS os, string key, string text)
    {
        var field = Nodes(JsonDocument.Parse(await ViewOf(os)).RootElement).FirstOrDefault(n => n.TryGetProperty("key", out var k) && k.GetString() == key && n.TryGetProperty("onSubmit", out _));
        Assert.True(field.ValueKind == JsonValueKind.Object, $"The view has no field '{key}'.");
        var command = field.GetProperty("onSubmit");
        var result = await AppUiEndpoints.Act(new($"{Brain}/apps/files", command.GetProperty("target").GetString(), command.GetProperty("signal").GetString(),
            command.TryGetProperty("args", out var args) ? args.Clone() : null, text), new Proxies(os), Brain, Ct, Caller());
        Assert.True(Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode == StatusCodes.Status200OK, $"{key}: {JsonSerializer.Serialize((result as IValueHttpResult)?.Value)}");
        await os.Idle();
    }

    // The text the view gives the field keyed <paramref name="key"/>: what it shows while the person is not typing.
    private static string? Shown(string view, string key)
        => Nodes(JsonDocument.Parse(view).RootElement).Where(n => n.TryGetProperty("key", out var k) && k.GetString() == key && n.TryGetProperty("onSubmit", out _))
            .Select(n => n.GetProperty("onSubmit").TryGetProperty("args", out var args) && args.TryGetProperty("uiValue", out var value) ? value.GetString() : null)
            .FirstOrDefault();

    private static async Task<string> ViewOf(TestOS os)
    {
        await os.Idle();
        return (await Screen(os).Read() ?? throw new InvalidOperationException("Files has no view.")).View.GetRawText();
    }

    // Presses a button as the client does: its command, as the view carries it, posted to /act.
    private static async Task Press(TestOS os, string key)
    {
        var command = Command(await ViewOf(os), key);
        var result = await AppUiEndpoints.Act(new($"{Brain}/apps/files", command.Target, command.Signal, command.Args), new Proxies(os), Brain, Ct, Caller());
        Assert.True(Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode == StatusCodes.Status200OK, $"{key}: {JsonSerializer.Serialize((result as IValueHttpResult)?.Value)}");
        await os.Idle();
    }

    private static DigitalBrain.UI.Command Command(string view, string key)
    {
        var node = Nodes(JsonDocument.Parse(view).RootElement).FirstOrDefault(n => n.TryGetProperty("key", out var k) && k.GetString() == key && n.TryGetProperty("command", out _));
        Assert.True(node.ValueKind == JsonValueKind.Object, $"The view has no button '{key}'.");
        var command = node.GetProperty("command");
        return new(command.GetProperty("target").GetString()!, command.GetProperty("signal").GetString()!, command.TryGetProperty("args", out var args) ? args.Clone() : null);
    }

    // The key of the list row that opens the item named <paramref name="name"/>.
    private static string Key(string view, string name)
        => Nodes(JsonDocument.Parse(view).RootElement).Where(n => n.TryGetProperty("key", out var k) && k.GetString()!.StartsWith("item-open-", StringComparison.Ordinal)
                && n.TryGetProperty("label", out var label) && label.GetString() == name)
            .Select(n => n.GetProperty("key").GetString()!).FirstOrDefault()
            ?? throw new InvalidOperationException($"No row opens '{name}': " + string.Join(", ", Nodes(JsonDocument.Parse(view).RootElement)
                .Where(n => n.TryGetProperty("key", out var k) && k.GetString()!.StartsWith("item-open-", StringComparison.Ordinal)).Select(n => n.GetProperty("key").GetString() + "=" + n.GetProperty("label").GetString())));

    private static IEnumerable<JsonElement> Nodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var child in element.EnumerateObject().SelectMany(p => Nodes(p.Value))) { yield return child; }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().SelectMany(Nodes)) { yield return child; }
        }
    }

    private static async Task<string?> Text(InMemoryStorage storage, ItemId folder, string name)
    {
        var item = (await storage.List(folder, new ListQuery(), Ct)).Items.FirstOrDefault(i => i.Name == name);
        return item is null ? null : System.Text.Encoding.UTF8.GetString((await storage.Read(item.Id, null, Ct)).Bytes.Span);
    }

    private sealed class Proxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }

}
