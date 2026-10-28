using System.Net;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Views;

public sealed class AliasViewHttpFacts(FlutterHost host)
{
    [Fact]
    public async Task LegacyViewReadAndActionResolveCanonicalViewAndGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var (legacy, canonical) = await Redirect(person, ct);
        var oldView = host.Brain.Get<IView>(legacy + "/view");
        var view = host.Brain.Get<IView>(canonical + "/view");
        await oldView.Publish(new(2, 1, Ui.Button("go", "Old", "retired")));
        await view.Publish(new(9, 1, Ui.Button("go", "Ino", "canonical")));
        var snapshot = await person.Http.GetFromJsonAsync<UiView>(Node(person.BrainId, "view", legacy + "/view"), ct);
        Assert.NotNull(snapshot);
        Assert.Equal(9, snapshot.Generation);
        Assert.Equal("canonical", snapshot.Root.Action);
        await using var actions = await host.Brain.Observe<ViewAction>(view, ct);
        var request = Guid.NewGuid();
        using var delivered = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name = legacy + "/view", revision = 9, field = "go", action = "canonical", operationId = request }, ct);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        var action = await actions.NextAsync(ct: ct);
        Assert.Equal((canonical + "/view", 9L, "canonical", request), (action.Name, action.Generation, action.Action, action.RequestId));
        using var stale = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name = legacy + "/view", revision = 2, field = "go", action = "retired", operationId = Guid.NewGuid() }, ct);
        Assert.False(stale.IsSuccessStatusCode);
        Assert.Equal("retired", (await oldView.Read())!.Root.Action);
    }

    [Fact]
    public async Task ViewportResolvesTheMigratedPresentationWithoutChangingItsDurableTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var (legacy, canonical) = await Redirect(person, ct);
        using (person.AsApp(canonical))
        {
            await host.Brain.Get<IApp>(canonical).Present(0,
                new(Views: [new("full", new("view", canonical + "/view"))]));
        }
        var name = person.BrainId + "/apps/workspace/canvas";
        var viewport = host.Brain.Get<IViewport>(name);
        var target = new WorkspaceViewRef("legacy-window", legacy, "full", "");
        await viewport.Set(target, 0);
        using var snapshot = JsonDocument.Parse(await person.Http.GetStringAsync(Node(person.BrainId, "viewport", name), ct));
        Assert.Equal(canonical + "/view", snapshot.RootElement.GetProperty("root").GetProperty("name").GetString());
        Assert.Equal("view", snapshot.RootElement.GetProperty("root").GetProperty("kind").GetString());
        Assert.Equal(legacy, snapshot.RootElement.GetProperty("target").GetProperty("app").GetString());
        Assert.Equal(target, (await viewport.Read()).Target);
    }

    [Fact]
    public async Task MigratedAppKeepsLegacyChatAndComposerAddressesAndRejectsOtherBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var (legacy, canonical) = await Redirect(person, ct);
        var oldComposer = host.Brain.Get<IComposer>(legacy + "/composer");
        var newComposer = host.Brain.Get<IComposer>(canonical + "/composer");
        await oldComposer.Configure("legacy-source", 3);
        await newComposer.Configure("canonical-source", 7);
        await newComposer.Edit("canonical-source", "keep canonical draft");
        using var edit = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "composer", name = legacy + "/composer", context = "legacy-source", action = "edit", value = "old input", selectionRevision = 3 }, ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var oldInput = await person.Http.GetFromJsonAsync<ComposerState>(Node(person.BrainId, "composer", legacy + "/composer"), ct);
        Assert.Equal("old input", oldInput!.Draft);
        Assert.Equal("legacy-source", oldInput.Context);
        Assert.Equal("keep canonical draft", (await newComposer.Read()).Draft);
        var chat = host.Brain.Get<IChat>(legacy + "/chat");
        await chat.Project(new("legacy-source", 3, 1, [new("message", ChatRole.Assistant, "old history")]));
        await host.Brain.Get<IChat>(canonical + "/chat").Project(new("canonical-source", 7, 1, [new("message", ChatRole.Assistant, "new history")]));
        var oldChat = await person.Http.GetFromJsonAsync<ChatState>(Node(person.BrainId, "chat", legacy + "/chat"), ct);
        Assert.Equal("legacy-source", oldChat!.Projection!.Source);
        Assert.Equal("old history", Assert.Single(oldChat.Projection.Messages).Text);
        var foreign = "other/brain/apps/assistant/view";
        using var deniedRead = await person.Http.GetAsync(Node(person.BrainId, "view", foreign), ct);
        Assert.False(deniedRead.IsSuccessStatusCode);
        using var deniedAction = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
            new { kind = "view", name = foreign, revision = 9, field = "go", action = "canonical", operationId = Guid.NewGuid() }, ct);
        Assert.False(deniedAction.IsSuccessStatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => SiloNeurons.Get<IAppMigration>(host.Brain.Services, legacy).Redirect("alias-http", "other/brain/apps/ino"));
        Assert.Equal(canonical, (await host.Brain.Get<IApp>(legacy).Read()).MigrationTarget);
    }

    private async Task<(string Legacy, string Canonical)> Redirect(IntoChat.OS.Testing.Person person, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var legacy = IApp.Key(person.BrainId, "assistant");
        var canonical = IApp.Key(person.BrainId, "ino");
        var manifest = new AppManifest("Chat", "Chat", [], [], []);
        await host.Brain.Get<IApp>(legacy).Create(manifest, "legacy");
        await host.Brain.Get<IApp>(canonical).Create(manifest, "canonical");
        await SiloNeurons.Get<IAppMigration>(host.Brain.Services, legacy).Pause("alias-http");
        await SiloNeurons.Get<IAppMigration>(host.Brain.Services, canonical).Pause("alias-http");
        await SiloNeurons.Get<IAppMigration>(host.Brain.Services, legacy).Redirect("alias-http", canonical);
        return (legacy, canonical);
    }

    private static string Node(string brain, string kind, string name)
        => $"/brains/{brain}/apps/node?kind={kind}&name={Uri.EscapeDataString(name)}";
}
