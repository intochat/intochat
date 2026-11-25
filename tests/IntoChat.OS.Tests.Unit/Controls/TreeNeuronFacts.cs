using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;
using SelectCommand = DigitalBrain.UI.Commands.Select;
using TreeViewNode = DigitalBrain.UI.Controls.TreeView;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record TreeParentState;

/*
# Tree parent

A test parent that slots two trees over the same nodes: one that owns its selection and expanded folders, one whose selection and
expanded folders the parent owns (so it can open the tree to a selected node).
*/
[Neuron("test.tree-parent")]
public sealed class TreeParent : Screen<TreeParentState>
{
    internal static readonly IReadOnlyList<TreeItem> Nodes =
        [new("code", "code", IsFolder: true), new("app", "app.cs", "code"), new("docs", "docs", IsFolder: true), new("readme", "README.md")];

    protected override View Render(TreeParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<TreeNeuron, TreeProps>("free", new TreeProps(Nodes)),
            Child<TreeNeuron, TreeProps>("owned", new TreeProps(Nodes, SelectedId: "app", ExpandedIds: ["code"])),
        ]));
    }
}

public sealed class TreeNeuronFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.tree-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public TreeNeuronFacts()
    {
        _os.Register<TreeParent>();
        _os.Register<TreeNeuron>();
    }

    private Task Send<T>(string slot, string alias, T command) where T : Signal
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    private Task Select(string slot, string id) => Send(slot, SelectionRefusals.SelectSignal, new SelectCommand(id));

    private Task Expand(string slot, string id, bool isExpanded) => Send(slot, TreeRefusals.ExpandNodeSignal, new ExpandNode(id, isExpanded));

    private NeuronId Tree(string slot) => new(Brain, "control.tree", ScreenExtensions.ChildAddress(ParentId, slot));

    private async Task<TreeViewNode> Drawn(string slot)
    {
        var view = await _os.Get<IScreen>(Tree(slot)).Read() ?? throw new InvalidOperationException("no view");
        return Assert.IsType<TreeViewNode>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    private Signal[] Published(string slot) => [.. _os.Journal(Brain).Signals(Tree(slot)).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Expanding_a_folder_stores_it_and_signals_NodeExpanded_once()
    {
        await Expand("free", "code", true);
        await Expand("free", "code", true);
        await Expand("free", "docs", true);
        await Expand("free", "code", false);

        Assert.Equal([new NodeExpanded("code", true), new NodeExpanded("docs", true), new NodeExpanded("code", false)], Published("free"));
        Assert.Equal(["docs"], (await Drawn("free")).ExpandedIds);
    }

    [Fact]
    public async Task Selecting_a_node_stores_it_and_signals_SelectionChanged_once()
    {
        await Select("free", "readme");
        await Select("free", "readme");

        Assert.Equal([new SelectionChanged("readme")], Published("free"));
        Assert.Equal("readme", (await Drawn("free")).SelectedId);
    }

    [Theory]
    [InlineData(TreeRefusals.ExpandNodeSignal, "lib", SelectionRefusals.UnknownItem)]
    [InlineData(TreeRefusals.ExpandNodeSignal, "readme", TreeRefusals.NotAFolder)]
    [InlineData(SelectionRefusals.SelectSignal, "lib", SelectionRefusals.UnknownItem)]
    public async Task A_node_the_props_cannot_expand_or_select_is_refused_and_the_tree_is_not_reached(string alias, string id, string reason)
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => alias == SelectionRefusals.SelectSignal
            ? Select("free", id)
            : Expand("free", id, true));

        Assert.Equal(reason, refused.Reason);
        Assert.False(_os.IsActive(Tree("free")));
    }

    [Fact]
    public async Task A_controlled_tree_always_signals_and_stores_neither_selection_nor_expanded_folders()
    {
        await Expand("owned", "code", true);
        await Expand("owned", "docs", true);
        await Select("owned", "app");

        Assert.Equal([new NodeExpanded("code", true), new NodeExpanded("docs", true), new SelectionChanged("app")], Published("owned"));
        var drawn = await Drawn("owned");
        Assert.Null(drawn.SelectedId);
        Assert.Empty(drawn.ExpandedIds);
    }

    [Fact]
    public void The_gate_names_the_expand_command_alias()
        => Assert.Equal(TreeRefusals.ExpandNodeSignal, typeof(ExpandNode).GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias);

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
