/*
# Query table

A control neuron: a table that stores a query, not rows. It owns the filter, the sort (column and direction) and the page, and signals
TableChanged on every change. Its title, the columns it shows and where its rows come from are props its parent writes on the slot;
the parent refuses a Sort naming a column the props do not show (control.unknownColumn) and a Page below 1 (control.pageOutOfRange).

The rows come from one of two places. Inline rows (props Rows, each with an id and cells) are the parent's: the neuron never reads or
holds them, and the client filters, sorts and pages them by the stored query, because a control sees its props only while a command
runs and so cannot draw rows that change with the parent. Without inline rows the rows are read from the table source at the props'
address. Render runs inside the save and cannot await, so each command asks the source for the new page before it saves the query,
and holds that one page in a field that is never persisted. After a reactivation the view says it is not loaded; the client sends a
Page for the current page on first drawing it, which reads it again. A command without props, an address that names no table source
of this brain (never called), or a source that throws is logged and drawn as the table's error line; it never fails the command or
the render.

An inline row can be selected by id: SelectRow stores the id and signals RowSelected, or, when the parent owns the selection (props
SelectedRowId), only signals it. The parent refuses an id its props do not hold (control.unknownRow); a table that reads a source has
no row ids, so it refuses every SelectRow. Named QueryTable because a `dataTable` data node already exists; it draws as a
queryTableView.
*/
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A query table: it owns the query and the selected row, reads one page from its source unless its rows are inline, and signals
/// <see cref="TableChanged"/> and <see cref="RowSelected"/>.</summary>
[Neuron("control.queryTable"), Emits(typeof(TableChanged)), Emits(typeof(RowSelected))]
public sealed partial class QueryTableNeuron : ControlNeuron<QueryTableState, QueryTableProps>, IQueryTableControl
{
    [Slot(Required = false)]
    private partial SlotCollection<ITableSource> TableSources { get; }

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

    [On]
    public Task SelectRow(SelectRow command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RowId);
        if (Props?.SelectedRowId is not null) { return Publish(new RowSelected(command.RowId)); }
        return command.RowId == State.SelectedRowId ? Task.CompletedTask : Show(State with { SelectedRowId = command.RowId }, new RowSelected(command.RowId));
    }

    private async Task Change(QueryTableState next)
    {
        _shown = await Read(next).ConfigureAwait(true);
        await Show(next, new TableChanged(next.Filter, next.SortBy, next.Descending, next.Page)).ConfigureAwait(true);
    }

    // Asks the props' source for the page of `query`; a missing source or a failure becomes the error the table draws.
    private async Task<Shown> Read(QueryTableState query)
    {
        if (Props is not { } props)
        {
            Log.NoSource(GetLogger(), Id.ToString());
            return new Shown([], [], 0, "The table has no source.");
        }
        IReadOnlyList<string> columnIds = [.. props.Columns.Select(column => column.Id)];
        // Inline rows are the parent's and drawn by the client from the props: nothing is read.
        if (props.Rows is not null) { return new Shown(columnIds, [], 0, null); }
        // Only a table source of this brain is called: an address naming anything else is drawn as the error line, never called.
        var source = props.SourceAddress ?? "";
        if (!NeuronId.TryParse(source, out var address) || !TableSources.Exists(address))
        {
            Log.NotASource(GetLogger(), source, Id.ToString());
            return new Shown(columnIds, [], 0, "The table's source is not a table source.");
        }
        try
        {
            var page = await TableSources[address].Query(new TableQuery(query.Filter, query.SortBy, query.Descending, query.Page, QueryTableState.PageSize)).ConfigureAwait(true);
            IReadOnlyList<IReadOnlyList<string>> rows = [.. page.Rows.Select(row => (IReadOnlyList<string>)[.. columnIds.Select(id => row.TryGetValue(id, out var value) ? value : "")])];
            return new Shown(columnIds, rows, page.Total, null);
        }
#pragma warning disable CA1031 // A source is another neuron: whatever it throws is drawn as the table's error line, and logged.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Log.SourceFailed(GetLogger(), error, source, Id.ToString());
            return new Shown(columnIds, [], 0, "The table's source did not answer.");
        }
    }

    protected override View Render(QueryTableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var shown = _shown;
        return new(Key, new QueryTableView(Key, state.Filter, state.SortBy, state.Descending, state.Page, QueryTableState.PageSize,
            shown?.Total ?? 0, shown is not null, shown?.ColumnIds ?? [], shown?.Rows ?? [], shown?.Error, state.SelectedRowId));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "The table source {Source} of {Table} failed")]
        public static partial void SourceFailed(ILogger logger, Exception error, string source, string table);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The table {Table} got a command without props, so it has no source")]
        public static partial void NoSource(ILogger logger, string table);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The table {Table} was not given a table source: {Source}")]
        public static partial void NotASource(ILogger logger, string source, string table);
    }
}
