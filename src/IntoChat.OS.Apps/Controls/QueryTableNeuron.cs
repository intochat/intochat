/*
# Query table

A control neuron: a table that stores a query, not rows. It owns the filter, the sort (column and direction) and the page, and signals
TableChanged on every change. Its title, the columns it shows and the address of the source its rows come from are props its parent
writes on the slot; the parent refuses a Sort naming a column the props do not show (control.unknownColumn) and a Page below 1
(control.pageOutOfRange).

Render runs inside the save and cannot await, so the rows are read when the query changes: each command asks the source for the new
page before it saves the query, and holds that one page in a field that is never persisted. After a reactivation the view says it is
not loaded until a command (a Page for the current page) reads it again. A source that throws or cannot be found is logged and drawn
as the table's error line; it never fails the command or the render. Named QueryTable because a `dataTable` data node already exists;
it draws as a queryTableView.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A query table: it owns the query, reads one page from its source, and signals <see cref="TableChanged"/>.</summary>
[Neuron("control.queryTable"), Emits(typeof(TableChanged))]
public sealed partial class QueryTableNeuron : ControlNeuron<QueryTableState, QueryTableProps>
{
    // The page read for the current query: the only rows the table holds, in memory and never saved.
    private Shown? _shown;

    private sealed record Shown(IReadOnlyList<string> ColumnIds, IReadOnlyList<IReadOnlyList<string>> Rows, int Total, string? Error);

    [On]
    public Task Sort(Sort command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Column);
        if (Props is { } props && props.Columns.All(column => column.Id != command.Column))
        {
            throw new ArgumentException($"Column '{command.Column}' is not shown.", nameof(command));
        }
        var descending = State.SortBy == command.Column && !State.Descending;
        return Change(State with { SortBy = command.Column, Descending = descending, Page = 1 });
    }

    [On]
    public Task Filter(Filter command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Change(State with { Filter = command.Text ?? "", Page = 1 });
    }

    [On]
    public Task Page(Page command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Number, 1, nameof(command));
        return Change(State with { Page = command.Number });
    }

    private async Task Change(QueryTableState next)
    {
        _shown = await Read(next).ConfigureAwait(true);
        await Show(next, new TableChanged(next.Filter, next.SortBy, next.Descending, next.Page)).ConfigureAwait(true);
    }

    // Asks the props' source for the page of `query`; a missing source or a failure becomes the error the table draws.
    private async Task<Shown> Read(QueryTableState query)
    {
        if (Props is not { } props) { return new Shown([], [], 0, "The table has no source."); }
        IReadOnlyList<string> columnIds = [.. props.Columns.Select(column => column.Id)];
        try
        {
            if (!NeuronId.TryParse(props.SourceAddress, out var address)) { throw new ArgumentException($"'{props.SourceAddress}' is not a neuron address."); }
            var page = await Get<ITableSource>(address).Query(new TableQuery(query.Filter, query.SortBy, query.Descending, query.Page, QueryTableState.PageSize)).ConfigureAwait(true);
            IReadOnlyList<IReadOnlyList<string>> rows = [.. page.Rows.Select(row => (IReadOnlyList<string>)[.. columnIds.Select(id => row.TryGetValue(id, out var value) ? value : "")])];
            return new Shown(columnIds, rows, page.Total, null);
        }
#pragma warning disable CA1031 // A source is another neuron: whatever it throws is drawn as the table's error line, and logged.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Log.SourceFailed(GetLogger(), error, props.SourceAddress, Id.ToString());
            return new Shown(columnIds, [], 0, "The table's source did not answer.");
        }
    }

    protected override View Render(QueryTableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var shown = _shown;
        return new(Key, new QueryTableView(Key, state.Filter, state.SortBy, state.Descending, state.Page, QueryTableState.PageSize,
            shown?.Total ?? 0, shown is not null, shown?.ColumnIds ?? [], shown?.Rows ?? [], shown?.Error));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "The table source {Source} of {Table} failed")]
        public static partial void SourceFailed(ILogger logger, Exception error, string source, string table);
    }
}
