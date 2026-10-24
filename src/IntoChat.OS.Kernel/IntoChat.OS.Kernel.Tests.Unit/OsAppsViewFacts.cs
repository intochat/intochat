using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The Apps app as a value tree: the library rows, one app's detail with what it asks for, and the activations its
// buttons carry. Nothing here installs; the facts read the tree and the activation JSON.
public sealed class OsAppsViewFacts
{
    private const string App = "b1/apps/apps";

    private static AppDetail Detail(string status = "available", string? installed = null, AppRequirements? requires = null,
        AppRequirements? consented = null, AppSlot[]? slots = null, string publisher = "os", string app = "calculator") => new(
        publisher, app, "Calculator", "Adds things up.", null, "1.2.0", installed, installed is null ? null : "1.1.0", false,
        requires ?? new(), consented, slots ?? [], status, status == "unavailable" ? AppsCopy.ReleaseGone : null);

    private static AppsModel Model(AppDetail? detail = null, string? share = "https://os.example.test/#/open?application=calculator&publisher=os",
        bool confirm = false, IReadOnlyList<AppDetail>? installed = null, IReadOnlyList<AppDetail>? available = null) => new(
        installed ?? [Detail("installed", "calculator"), Detail("update", "notes", app: "notes", publisher: "vlad")],
        available ?? [Detail(), Detail("unavailable", app: "gone")],
        [new("others", "", "gmail", "Gmail", "Connect your Gmail account.", null, false, false, null, "connection", "gmail"),
         new("others", "", "github", "GitHub", "Connected.", null, true, false, null, "connection", "github")],
        detail, share, confirm, null, null);

    private static IEnumerable<UiNode> All(UiNode node) => node.Children.SelectMany(All).Prepend(node);

    private static UiNode Find(UiNode root, string key) => All(root).Single(node => node.Key == key);

    private static UiNode Render(AppsModel m) => AppsView.Render(App, m);

    [Fact]
    public void ShippedPublisherDisplaysAsIntochat()
    {
        Assert.Equal("intochat/calculator", AppsNames.Display("os", "calculator"));
        Assert.Equal("vlad/notes", AppsNames.Display("vlad", "notes"));
    }

    [Fact]
    public void ShareLinkIsAbsoluteAndEscaped()
    {
        Assert.Equal("https://os.example.test/#/open?application=calculator&publisher=os&release=1.2.0",
            AppsNames.ShareLink("https://os.example.test/", "os", "calculator", "1.2.0"));
        Assert.Equal("https://os.example.test:8443/#/open?application=my%20app&publisher=os",
            AppsNames.ShareLink("https://user:pw@os.example.test:8443/base/?q=1", "os", "my app", null));
        Assert.Null(AppsNames.ShareLink(null, "os", "calculator", null));
        Assert.Null(AppsNames.ShareLink("http://os.example.test", "os", "calculator", null));
        Assert.Equal("http://localhost:5000/#/open?application=calculator&publisher=os",
            AppsNames.ShareLink("http://localhost:5000", "os", "calculator", null));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("app:os/calculator", "app:os/calculator")]
    [InlineData("app:vlad/notes@1.2.0", "app:vlad/notes@1.2.0")]
    [InlineData("installed:notes", "installed:notes")]
    [InlineData("app:OS/calculator", "")]
    [InlineData("app:os/calculator@1.0", "")]
    [InlineData("app:os/calculator extra", "")]
    [InlineData("calculator", "")]
    public void OnlyWellFormedTargetsOpenADetail(string input, string target) => Assert.Equal(target, AppsNames.Parse(input).ToString());

    [Fact]
    public void LibraryRowsShowNameDisplayVersionAndStatus()
    {
        var tree = Render(Model());
        Assert.Equal(["apps-title", "installed", "available", "connections"], tree.Children.Select(node => node.Key));
        var row = Find(tree, "app-installed-1");
        Assert.Equal(UiNodeKind.Row, row.Kind);
        Assert.Equal([0, 160, 72, 96, 88], row.Extents!);
        Assert.Equal(["Calculator", "vlad/notes", "v1.1.0", "Update available"], row.Children.Take(4).Select(node => node.Text));
        Assert.Equal(UiTone.Working, row.Children[3].Tone);
        Assert.Equal("detail:installed/1", row.Children[4].Action);
        Assert.Equal(("Installed", UiTone.Done), (Find(tree, "status-installed-0").Text, Find(tree, "status-installed-0").Tone));
        Assert.Equal("intochat/calculator", Find(tree, "display-installed-0").Text);
        Assert.Equal(("Available", UiTone.Neutral), (Find(tree, "status-available-0").Text, Find(tree, "status-available-0").Tone));
        Assert.Equal(("Unavailable", UiTone.Error), (Find(tree, "status-available-1").Text, Find(tree, "status-available-1").Tone));
        Assert.Equal("v1.2.0", Find(tree, "version-available-0").Text);
    }

    [Fact]
    public void ConnectionsKeepTheirGalleryKeys()
    {
        var tree = Render(Model());
        var connect = Find(tree, "connect-connection/gmail");
        Assert.Equal((UiNodeKind.Reference, UIVocabulary.ButtonType, App + "/connect/gmail"), (connect.Kind, connect.Reference!.Kind, connect.Reference.Name));
        Assert.Equal(("GitHub", "Connected."), (Find(tree, "entry-connection/github").Text, Find(tree, "entry-connection/github").Detail));
        Assert.DoesNotContain(All(tree), node => node.Key == "connect-connection/github");
    }

    [Fact]
    public void DetailShowsRequirementsByKind()
    {
        var detail = Detail(requires: new(["person.birthDate"], ["gmail"], ["WEATHER_TOKEN"]), slots: [new("mail", "gmail")]);
        var tree = Render(Model(detail));
        Assert.Equal("back", Find(tree, "detail-header").Children[0].Action);
        Assert.Equal("intochat/calculator", Find(tree, "detail-display").Text);
        Assert.Equal(("Adds things up.", "Made by intochat"), (Find(tree, "about").Text, Find(tree, "about").Detail));
        Assert.Equal("Latest 1.2.0", Find(tree, "versions").Text);
        var requires = Find(tree, "requires");
        Assert.Equal("This app asks for", requires.Text);
        Assert.Equal("person.birthDate", Assert.Single(Find(tree, "requires-data").Children).Text);
        var slot = Find(tree, "slot-row-0");
        Assert.Equal("mail", slot.Children[0].Text);
        Assert.Equal((UIVocabulary.SelectType, App + "/slot/0"), (slot.Children[1].Reference!.Kind, slot.Children[1].Reference!.Name));
        Assert.Equal(App + "/slot-new/0", slot.Children[2].Reference!.Name);
        Assert.Equal("WEATHER_TOKEN", Find(tree, "secret-0").Text);
        Assert.Equal(App + "/to-variables", Find(tree, "secrets-settings").Reference!.Name);
        Assert.Equal(App + "/install", Find(tree, "install").Reference!.Name);
        using var variables = JsonDocument.Parse(AppsView.VariablesActivation("b1"));
        Assert.Equal(("workspace", "settings", "open", "variables"),
            (variables.RootElement.GetProperty("kind").GetString(), variables.RootElement.GetProperty("application").GetString(),
             variables.RootElement.GetProperty("operation").GetString(), variables.RootElement.GetProperty("input").GetString()));
    }

    [Fact]
    public void UpdateWithAddedRequirementsSaysAsksForMore()
    {
        var detail = Detail("update", "calculator", requires: new(["person.birthDate", "person.name"]), consented: new(["person.name"]));
        var tree = Render(Model(detail));
        Assert.Equal("This update asks for more:", Find(tree, "requires").Text);
        Assert.Equal(["person.birthDate"], Find(tree, "requires-data").Children.Select(node => node.Text));
        Assert.Equal("Installed 1.1.0 · Latest 1.2.0", Find(tree, "versions").Text);
        Assert.Equal(App + "/update", Find(tree, "update").Reference!.Name);
        Assert.DoesNotContain(All(tree), node => node.Key == "install");
        using var update = JsonDocument.Parse(AppsView.UpdateActivation("b1", detail));
        Assert.Equal(("update", "calculator", AppConsent.Of(detail.Requires)),
            (update.RootElement.GetProperty("kind").GetString(), update.RootElement.GetProperty("name").GetString(), update.RootElement.GetProperty("consent").GetString()));
        var same = Render(Model(detail with { Consented = detail.Requires }));
        Assert.Equal("This app asks for", Find(same, "requires").Text);
    }

    [Fact]
    public void UnavailableHasNoInstall()
    {
        var tree = Render(Model(Detail("unavailable")));
        var badge = Find(tree, "unavailable");
        Assert.Equal((UiNodeKind.Badge, "This release is no longer available.", UiTone.Error), (badge.Kind, badge.Text, badge.Tone));
        Assert.DoesNotContain(All(tree), node => node.Key is "install" or "update");
    }

    [Fact]
    public void RemoveAsksInAModal()
    {
        var detail = Detail("installed", "calculator");
        var closed = Render(Model(detail));
        Assert.Equal("remove-ask", Find(closed, "remove-ask").Action);
        Assert.DoesNotContain(All(closed), node => node.Kind == UiNodeKind.Modal);
        var tree = Render(Model(detail, confirm: true));
        var modal = Find(tree, "confirm-remove");
        Assert.Equal(("Remove Calculator?", "Its data stays in this brain."), (modal.Text, modal.Detail));
        Assert.Equal(App + "/remove", Find(modal, "remove-go").Reference!.Name);
        Assert.Equal(("Cancel", "remove-cancel"), (Find(modal, "remove-cancel").Text, Find(modal, "remove-cancel").Action));
        using var remove = JsonDocument.Parse(AppsView.RemoveActivation("b1", detail));
        Assert.Equal(("remove", "calculator", true),
            (remove.RootElement.GetProperty("kind").GetString(), remove.RootElement.GetProperty("name").GetString(), remove.RootElement.GetProperty("confirmed").GetBoolean()));
        // The OS's roots stay installed: no Remove is offered.
        var settings = Render(Model(Detail("installed", "settings", app: "settings"), confirm: true));
        Assert.DoesNotContain(All(settings), node => node.Key is "remove-ask" or "confirm-remove");
    }

    [Fact]
    public void NoPublicOriginShowsSharingOff()
    {
        var shared = Render(Model(Detail()));
        Assert.Equal("copy:https://os.example.test/#/open?application=calculator&publisher=os", Find(shared, "share").Action);
        Assert.Equal("Copy link", Find(shared, "share").Text);
        var off = Render(Model(Detail(), share: null));
        Assert.Equal("Sharing isn't set up on this host.", Find(off, "share-off").Text);
        Assert.DoesNotContain(All(off), node => node.Key == "share");
    }

    [Fact]
    public void InstallActivationCarriesConsentAndBindingsButNotNewChoices()
    {
        var detail = Detail(requires: new(["person.name"], ["gmail", "salesforce"]), slots: [new("mail", "gmail"), new("crm", "salesforce")]);
        var operation = Guid.Parse("00000000-0000-0000-0000-000000000009");
        using var install = JsonDocument.Parse(AppsView.InstallActivation("b1", detail, ["gmail-work", AppsView.NewChoice], operation));
        var root = install.RootElement;
        Assert.Equal(("install", "b1", "Calculator"), (root.GetProperty("kind").GetString(), root.GetProperty("brainId").GetString(), root.GetProperty("title").GetString()));
        var request = root.GetProperty("request");
        Assert.Equal(("os", "calculator"), (request.GetProperty("ref").GetProperty("publisher").GetString(), request.GetProperty("ref").GetProperty("app").GetString()));
        Assert.Equal(AppConsent.Of(detail.Requires), request.GetProperty("consent").GetString());
        Assert.Equal(operation, request.GetProperty("operationId").GetGuid());
        var bindings = request.GetProperty("bindings");
        Assert.Equal("gmail-work", bindings.GetProperty("mail").GetString());
        Assert.False(bindings.TryGetProperty("crm", out _));
        // The request deserializes as the server's InstallRequest.
        var parsed = JsonSerializer.Deserialize<InstallRequest>(request.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((new AppRef("os", "calculator"), operation), (parsed.Ref, parsed.OperationId));
        using var none = JsonDocument.Parse(AppsView.InstallActivation("b1", detail, [AppsView.NewChoice, null], operation));
        Assert.False(none.RootElement.GetProperty("request").TryGetProperty("bindings", out _));
    }
}
