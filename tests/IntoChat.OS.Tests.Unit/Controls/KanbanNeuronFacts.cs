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

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record KanbanParentState;

/*
# Kanban parent

A test parent that slots two boards over the same columns and cards: one whose selection it leaves to the board, one whose selection it owns.
The Doing column holds at most two cards.
*/
[Neuron("test.kanban-parent")]
public sealed class KanbanParent : Screen<KanbanParentState>
{
    internal static readonly IReadOnlyList<KanbanColumnItem> Columns = [new("todo", "To do"), new("doing", "Doing", WipLimit: 2), new("done", "Done")];

    internal static readonly IReadOnlyList<KanbanCardItem> Cards =
        [new("a", "todo", "Write"), new("b", "todo", "Review"), new("c", "doing", "Ship"), new("d", "doing", "Test")];

    protected override View Render(KanbanParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<KanbanNeuron, KanbanProps>("board", new KanbanProps("Work", Columns, Cards)),
            Child<KanbanNeuron, KanbanProps>("owned", new KanbanProps("Owned", Columns, Cards, SelectedCardId: "a")),
        ]));
    }
}

// A board's columns and cards are its parent's props: a move only signals CardMoved and the parent re-renders the cards. The parent's
// hop refuses a move or a selection its props cannot hold before the board is reached, so a refused move leaves the drawn board as it was.
public sealed class KanbanNeuronFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.kanban-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public KanbanNeuronFacts()
    {
        _os.Register<KanbanParent>();
        _os.Register<KanbanNeuron>();
    }

    private Task Send<T>(string slot, string alias, T command) where T : Signal
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    private Task Move(string slot, string card, string column, int index) => Send(slot, KanbanRefusals.MoveCardSignal, new MoveCard(card, column, index));

    private Task Select(string slot, string card) => Send(slot, SelectionRefusals.SelectSignal, new SelectCommand(card));

    private NeuronId Board(string slot) => new(Brain, "control.kanban", ScreenExtensions.ChildAddress(ParentId, slot));

    private async Task<KanbanView> Drawn(string slot)
    {
        var view = await _os.Get<IScreen>(Board(slot)).Read() ?? throw new InvalidOperationException("no view");
        return Assert.IsType<KanbanView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    private Signal[] Published(string slot) => [.. _os.Journal(Brain).Signals(Board(slot)).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task A_move_signals_CardMoved_and_the_board_stores_no_card()
    {
        await Move("board", "a", "done", 0);

        Assert.Equal([new CardMoved("a", "done", 0)], Published("board"));
        Assert.Equal(new KanbanView("board", null), await Drawn("board"));
        Assert.Single(_os.Journal(Brain).Signals(Board("board")), entry => entry.Signal is StateChanged);
    }

    [Fact]
    public async Task A_move_within_a_full_column_is_allowed()
    {
        await Move("board", "d", "doing", 0);

        Assert.Equal([new CardMoved("d", "doing", 0)], Published("board"));
    }

    [Theory]
    [InlineData("z", "done", 0, KanbanRefusals.UnknownCard)]
    [InlineData("a", "later", 0, KanbanRefusals.UnknownColumn)]
    [InlineData("a", "done", 1, KanbanRefusals.IndexOutOfRange)]
    [InlineData("a", "done", -1, KanbanRefusals.IndexOutOfRange)]
    [InlineData("a", "doing", 0, KanbanRefusals.ColumnFull)]
    public async Task A_move_the_props_cannot_hold_is_refused_and_the_board_is_not_reached(string card, string column, int index, string reason)
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Move("board", card, column, index));

        Assert.Equal(reason, refused.Reason);
        Assert.False(_os.IsActive(Board("board")));
    }

    [Fact]
    public async Task A_refused_move_leaves_the_drawn_board_unchanged()
    {
        await Select("board", "b");
        var before = await Drawn("board");

        await Assert.ThrowsAsync<ActivationRefusedException>(() => Move("board", "b", "doing", 0));

        Assert.Equal(before, await Drawn("board"));
        Assert.Equal([new SelectionChanged("b")], Published("board"));
    }

    [Fact]
    public async Task Selecting_a_card_stores_it_and_signals_SelectionChanged_once()
    {
        await Select("board", "c");
        await Select("board", "c");

        Assert.Equal([new SelectionChanged("c")], Published("board"));
        Assert.Equal(new KanbanView("board", "c"), await Drawn("board"));
    }

    [Fact]
    public async Task Selecting_a_card_the_props_do_not_hold_is_refused()
    {
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Select("board", "z"));

        Assert.Equal(SelectionRefusals.UnknownItem, refused.Reason);
        Assert.False(_os.IsActive(Board("board")));
    }

    [Fact]
    public async Task A_controlled_selection_always_signals_and_stores_nothing()
    {
        await Select("owned", "b");
        await Select("owned", "a");
        await Select("owned", "a");

        Assert.Equal([new SelectionChanged("b"), new SelectionChanged("a"), new SelectionChanged("a")], Published("owned"));
        Assert.Null((await Drawn("owned")).SelectedCardId);
    }

    [Fact]
    public void The_gate_names_the_move_command_alias()
        => Assert.Equal(KanbanRefusals.MoveCardSignal, typeof(MoveCard).GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias);

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
