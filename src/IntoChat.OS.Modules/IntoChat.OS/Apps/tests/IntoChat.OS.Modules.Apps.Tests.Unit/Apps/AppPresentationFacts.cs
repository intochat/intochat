using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using PresentationCollaborator = IntoChat.OS.Modules.Apps.Presentations.AppPresentation;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppPresentationFacts
{
    public static TheoryData<Node, UiNodeKind, string> Nodes() => new()
    {
        { new Column([new Text("hello")]) { Gap = 8 }, UiNodeKind.Column, "hello" },
        { new Row([new Text("world")]) { Gap = 4 }, UiNodeKind.Row, "world" },
        { new Grid(2, [new Text("cell")], Gap: 6), UiNodeKind.Card, "\"_ui\":\"grid\"" },
        { new SplitPane(new Text("left"), new Text("right"), LeadingExtent: 200, Gap: 10), UiNodeKind.Row, "left" },
        { new Surface([new Text("body")], Title: "Panel", Subtitle: "Sub"), UiNodeKind.Card, "\"_ui\":\"surface\"" },
        { new Tabs([new TabItem("a", "First", new ButtonAction("app", "tab", ["a"]))], "a"), UiNodeKind.Tabs, "First" },
        { new Divider("Section"), UiNodeKind.Divider, "Section" },
        { new Spacer(16), UiNodeKind.Card, "\"_ui\":\"spacer\"" },
        { new Collection([new Entry("1", "Item 1", "Sub 1")]), UiNodeKind.List, "Item 1" },
        { new Heading("Main Heading", Level: 2, Subtitle: "Sub"), UiNodeKind.Text, "\"_ui\":\"heading\"" },
        { new Text("Plain text"), UiNodeKind.Text, "Plain text" },
        { new Text("Styled text") { Tone = "accent", Tabular = true, Mono = true, Size = 12 }, UiNodeKind.Text, "\"_ui\":\"text\"" },
        { new Badge("Ready", Tone: "ok", Icon: "check"), UiNodeKind.Badge, "Ready" },
        { new TagPill("Tag", Tone: "info", Icon: "tag"), UiNodeKind.Badge, "\"_ui\":\"tagpill\"" },
        { new Button("Save", new ButtonAction("app", "save", ["1"])) { Style = "primary", Small = true }, UiNodeKind.Button, "save:1" },
        { new TextField("query", "abc", "Search") { OnSubmit = new ButtonAction("app", "search", []) }, UiNodeKind.Card, "\"_ui\":\"textfield\"" },
        { new Switch("Dark Mode", true, new ButtonAction("app", "toggle", [])), UiNodeKind.Card, "\"_ui\":\"switch\"" },
        { new Select("Model", "gpt", [new SelectOption("gpt", "GPT")]), UiNodeKind.Card, "\"_ui\":\"select\"" },
        { new Progress("Halfway", 0.5, "50%"), UiNodeKind.Card, "\"_ui\":\"progress\"" },
        { new InfoBar("Notice", "Saved", Tone: "info", Action: new Button("OK", new ButtonAction("app", "ok", []))), UiNodeKind.Card, "\"_ui\":\"infobar\"" },
        { new Toast("Done!"), UiNodeKind.Toast, "Done!" },
        { new Card("Card Title", "Card Body", [new Text("Child")]), UiNodeKind.Card, "Card Title" },
        { new DataTable("Users", ["Name"], [["Alice"]], SourceNeuronUri: "neuron://owner/users"), UiNodeKind.Card, "\"_ui\":\"datatable\"" },
        { new Chart("bar", "Revenue", [new ChartPointNode("Q1", 10)], Prefix: "$"), UiNodeKind.Card, "\"_ui\":\"chart\"" },
        { new ApprovalCardNode("ap-1", "Send email", "Scope", [new ApprovalRowNode("To", "", "bob@example.com")], SystemName: "Mail", Status: "pending", OnApprove: new ButtonAction("app", "approve", []), OnReject: new ButtonAction("app", "deny", [])), UiNodeKind.Card, "\"_ui\":\"approval\"" },
        { new ChoiceCardNode("ch-1", "Pick one", [new ChoiceOptionNode("A", "Option A")], PickedIndex: 0), UiNodeKind.Card, "\"_ui\":\"choice\"" },
        { new DraftCardNode("dr-1", "email", "bob@example.com", "World", "alice@example.com", Subject: "Hello", Status: "draft", OnSend: new ButtonAction("app", "send", [])), UiNodeKind.Card, "\"_ui\":\"draft\"" },
        { new RecordCardNode("Contact", "Alice", [new RecordFieldNode("Role", "Owner")], Subtitle: "Team"), UiNodeKind.Card, "\"_ui\":\"record\"" },
        { new SecretCardNode("GitHub", "API access", SavedIn: "gh_token", Status: "saved"), UiNodeKind.Card, "\"_ui\":\"secret\"" },
        { new JobCardNode("Export", [new JobStepNode("Step 1", "running", "Working")], Progress: 0.4, Status: "running"), UiNodeKind.Card, "\"_ui\":\"job\"" },
        { new BrainSceneNode([new BrainGraphNode("n1", "Notes", "owner/apps/notes", "app", 1, 2, 3)], [new BrainGraphSynapse("n1", "n1", "Changed", true)]), UiNodeKind.Card, "\"_ui\":\"brain3d\"" },
        { new NeuronRef("chat", "neuron://owner/apps/ino/chat"), UiNodeKind.Card, "\"_ui\":\"neuronRef\"" },
        { new AppShellNode("shell", new Text("Side"), new Text("Body"), Toolbar: new Text("Top"), Inspector: new Text("Right")), UiNodeKind.Card, "\"_ui\":\"appshell\"" },
        { new NavItemNode("nav-general", "General", "settings", "nav:general", Selected: true, Badge: "2"), UiNodeKind.Card, "\"_ui\":\"navitem\"" },
        { new StatCardNode("stat-active", "Active", "42", Delta: "+3", Tone: BadgeTone.Done, Icon: "bolt"), UiNodeKind.Card, "\"_ui\":\"statcard\"" },
        { new WebBrowser("wb-1", "https://example.com", Title: "Preview", Height: 480), UiNodeKind.Card, "\"_ui\":\"webbrowser\"" },
    };

    [Theory]
    [MemberData(nameof(Nodes))]
    public void RenderMapsEveryNodeKindDeterministically(Node node, UiNodeKind expectedKind, string expectedMarker)
    {
        var state = new AppState { BrainScopeId = "owner", ProgramRevision = 3 };
        var collaborator = new PresentationCollaborator(state);

        var mappedNode = PresentationCollaborator.MapNode("n", node, "owner");
        Assert.Equal(expectedKind, mappedNode.Kind);
        var combined = (mappedNode.Detail ?? "") + "|" + System.Text.Json.JsonSerializer.Serialize(mappedNode);
        Assert.Contains(expectedMarker, combined, StringComparison.Ordinal);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapNode("n", node, "owner")),
            System.Text.Json.JsonSerializer.Serialize(mappedNode));

        var view = new View("Test View", new Column([node]));
        var rendered = collaborator.Render(view);
        var json = System.Text.Json.JsonSerializer.Serialize(rendered);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(AppNeuron.MapView(view, "owner")),
            json);
    }

    [Fact]
    public void PublishAndWithBlackboardProducePureStateTransitions()
    {
        var state = new AppState { BrainScopeId = "owner", ProgramRevision = 2 };
        var spec = new IntoChat.OS.Modules.Apps.Presentation(
            new Sphere("icon.svg", ["count"]),
            Views: [new PresentationView("main", new UiChildRef("view", "owner/apps/notes/view"))]);

        var published = new PresentationCollaborator(state).Publish(spec);
        Assert.NotNull(published);
        Assert.Null(state.Presentation);
        Assert.Equal(1, published!.Presentation!.Revision);

        // Idempotent publish returns null when unchanged
        Assert.Null(new PresentationCollaborator(published).Publish(spec));

        var updated = new PresentationCollaborator(published).WithBlackboard(new Dictionary<string, string> { ["count"] = "5" });
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Presentation!.Revision);
        Assert.Equal("5", updated.Presentation.Blackboard["count"]);
        Assert.Empty(published.Presentation.Blackboard);
    }
}
