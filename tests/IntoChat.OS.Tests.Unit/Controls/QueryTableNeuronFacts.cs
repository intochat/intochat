using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record TableParentState;

/*
# Table parent

A test parent that slots three query tables: one over the sample source, one over a source that throws, one over a recording source.
*/
[Neuron("test.table-parent")]
public sealed class TableParent : Screen<TableParentState>
{
    internal static readonly IReadOnlyList<QueryTableColumn> Columns = [new("id", "Id"), new("name", "Name"), new("status", "Status")];

    protected override View Render(TableParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<QueryTableNeuron, QueryTableProps>("people", new QueryTableProps("People", Columns, "local/control.sampleTableSource/people")),
            Child<QueryTableNeuron, QueryTableProps>("broken", new QueryTableProps("Broken", Columns, "local/test.failingTableSource/x")),
            Child<QueryTableNeuron, QueryTableProps>("wrong", new QueryTableProps("Wrong", Columns, "local/test.notATableSource/x")),
            Child<QueryTableNeuron, QueryTableProps>("probe", new QueryTableProps("Probe", Columns, "local/test.recordingTableSource/x")),
        ]));
    }
}

public sealed record FakeSourceState;

/*
# Failing table source

A table source that always throws.
*/
[Neuron("test.failingTableSource")]
public sealed class FailingTableSource : Neuron<FakeSourceState>, ITableSource
{
    public Task<IReadOnlyList<QueryTableColumn>> Columns() => throw new InvalidOperationException("down");

    public Task<TablePage> Query(TableQuery q) => throw new InvalidOperationException("down");
}

/*
# Recording table source

A table source that records the queries it is asked and answers with none.
*/
[Neuron("test.recordingTableSource")]
public sealed class RecordingTableSource : Neuron<FakeSourceState>, ITableSource
{
    internal static readonly List<TableQuery> Asked = [];

    public Task<IReadOnlyList<QueryTableColumn>> Columns() => Task.FromResult(TableParent.Columns);

    public Task<TablePage> Query(TableQuery q)
    {
        lock (Asked) { Asked.Add(q); }
        return Task.FromResult(new TablePage([], 0));
    }
}

/*
# Not a table source

A neuron that is not a table source; a table pointed at it must not call it.
*/
[Neuron("test.notATableSource")]
public sealed class NotATableSource : Neuron<FakeSourceState>;

public sealed class QueryTableNeuronFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.table-parent", "main");

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public QueryTableNeuronFacts()
    {
        _os.Register<TableParent>();
        _os.Register<QueryTableNeuron>();
        _os.Register<SampleTableSource>();
        _os.Register<FailingTableSource>();
        _os.Register<RecordingTableSource>();
        _os.Register<NotATableSource>();
    }

    private Task Send<T>(string slot, string alias, T command) where T : Signal
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    private NeuronId Table(string slot) => new(Brain, "control.queryTable", ScreenExtensions.ChildAddress(ParentId, slot));

    private async Task<QueryTableView> Drawn(string slot)
    {
        var view = await _os.Get<IScreen>(Table(slot)).Read() ?? throw new InvalidOperationException("no view");
        return Assert.IsType<QueryTableView>(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
    }

    private Signal[] Published(string slot) => [.. _os.Journal(Brain).Signals(Table(slot)).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    [Fact]
    public async Task Query_state_holds_no_rows()
    {
        await Send("people", "control.filter", new Filter("person"));

        var stored = JsonSerializer.SerializeToElement(new QueryTableState { Filter = "person" }, JsonSerializerOptions.Web);
        Assert.Equal(["descending", "filter", "page", "sortBy"], stored.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(QueryTableState).GetProperties(), p => p.PropertyType != typeof(string) && p.PropertyType != typeof(bool) && p.PropertyType != typeof(int));
        Assert.Equal(50, (await Drawn("people")).Rows.Count);
    }

    [Fact]
    public async Task Sort_and_filter_go_to_source()
    {
        lock (RecordingTableSource.Asked) { RecordingTableSource.Asked.Clear(); }

        await Send("probe", "control.filter", new Filter("ad"));
        await Send("probe", "control.sort", new Sort("name"));
        await Send("probe", "control.sort", new Sort("name"));

        lock (RecordingTableSource.Asked)
        {
            Assert.Equal([new TableQuery("ad", null, false, 1), new TableQuery("ad", "name", false, 1), new TableQuery("ad", "name", true, 1)], RecordingTableSource.Asked);
        }
    }

    [Fact]
    public async Task Filter_narrows_sample_rows_case_insensitively()
    {
        await Send("people", "control.filter", new Filter("PERSON 01"));

        var drawn = await Drawn("people");
        Assert.Equal(10, drawn.Total);
        Assert.All(drawn.Rows, row => Assert.StartsWith("Person 01", row[1], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_2_shows_rows_51_to_100()
    {
        await Send("people", "control.page", new Page(2));

        var drawn = await Drawn("people");
        Assert.Equal(120, drawn.Total);
        Assert.Equal(["id", "name", "status"], drawn.ColumnIds);
        Assert.Equal([.. Enumerable.Range(51, 50).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], drawn.Rows.Select(row => row[0]));
    }

    [Fact]
    public async Task Source_failure_renders_error_line()
    {
        await Send("broken", "control.page", new Page(1));

        var drawn = await Drawn("broken");
        Assert.NotNull(drawn.Error);
        Assert.Empty(drawn.Rows);
        Assert.True(drawn.Loaded);
    }

    [Fact]
    public async Task A_source_address_naming_no_table_source_draws_the_error_line_and_is_never_called()
    {
        await Send("wrong", "control.page", new Page(1));

        var drawn = await Drawn("wrong");
        Assert.NotNull(drawn.Error);
        Assert.Empty(drawn.Rows);
        Assert.False(_os.IsActive(new NeuronId(Brain, "test.notATableSource", "x")));
        Assert.Empty(_os.Journal(Brain).Signals(new NeuronId(Brain, "test.notATableSource", "x")));
    }

    [Fact]
    public async Task Unknown_column_sort_is_refused()
    {
        var sort = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send("people", "control.sort", new Sort("salary")));
        var page = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send("people", "control.page", new Page(0)));

        Assert.Equal(TableRefusals.UnknownColumn, sort.Reason);
        Assert.Equal(TableRefusals.PageOutOfRange, page.Reason);
        Assert.Empty(Published("people"));
    }

    [Fact]
    public async Task Every_change_publishes_TableChanged()
    {
        await Send("people", "control.filter", new Filter("p"));
        await Send("people", "control.sort", new Sort("id"));
        await Send("people", "control.page", new Page(2));

        Assert.Equal([new TableChanged("p", null, false, 1), new TableChanged("p", "id", false, 1), new TableChanged("p", "id", false, 2)], Published("people"));
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
