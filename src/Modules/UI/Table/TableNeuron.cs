using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Table.Signals;

namespace IntoChat.OS.Modules.UI.Table;

[Neuron("ui.table")]
internal sealed class TableNeuron : UiNeuronBase<TableState>, ITable
{
    public async Task<TableState> Bind(string sourceNeuronId, string title, IReadOnlyList<TableColumn> columns, int pageSize = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNeuronId);
        ArgumentNullException.ThrowIfNull(columns);
        var bound = State with
        {
            Title = title ?? "",
            Columns = [.. columns],
            SourceNeuronId = sourceNeuronId.Trim(),
            PageSize = Math.Clamp(pageSize, 1, 100),
            Page = 1,
            SelectedRowIndex = null,
        };
        return await ExecuteBoundQuery(bound);
    }

    public async Task<TableState> Render(string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "",
            Columns = [.. columns],
            Rows = rows.Select(static r => r.ToArray()).ToArray(),
            Total = rows.Count,
            SourceNeuronId = null,
            SelectedRowIndex = null,
            Status = rows.Count > 0 ? "ready" : "empty",
            Error = null,
        };
        await Save(next, Changed(next));
        return next;
    }

    public Task<TableState> Filter(string text)
        => UpdateQuery(State with { Filter = text ?? "", Page = 1, SelectedRowIndex = null });

    public Task<TableState> Sort(string columnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnId);
        if (State.Columns.All(c => !string.Equals(c.Id, columnId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Column '{columnId}' is not shown.", nameof(columnId));
        }
        var descending = string.Equals(State.SortBy, columnId, StringComparison.Ordinal) && !State.Descending;
        return UpdateQuery(State with { SortBy = columnId, Descending = descending, Page = 1, SelectedRowIndex = null });
    }

    public Task<TableState> Page(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return UpdateQuery(State with { Page = number, SelectedRowIndex = null });
    }

    public async Task<TableState> SelectRow(int? rowIndex)
    {
        int? resolved = rowIndex is >= 0 and var i && i < State.Rows.Length ? i : null;
        var next = State with { Version = State.Version + 1, SelectedRowIndex = resolved };
        await Save(next, Changed(next), new TableRowSelected(Id.FullKey(), resolved));
        return next;
    }

    public Task<TableState> Refresh()
        => string.IsNullOrWhiteSpace(State.SourceNeuronId) ? Task.FromResult(State) : ExecuteBoundQuery(State);

    public Task<TableState> Read() => Task.FromResult(State);

    private async Task<TableState> UpdateQuery(TableState next)
    {
        if (!string.IsNullOrWhiteSpace(next.SourceNeuronId))
        {
            return await ExecuteBoundQuery(next);
        }
        var updated = next with { Version = next.Version + 1 };
        await Save(updated, Changed(updated));
        return updated;
    }

    private async Task<TableState> ExecuteBoundQuery(TableState basis)
    {
        var sourceId = basis.SourceNeuronId!;
        if (!NeuronId.TryParse(sourceId, out var address) || !Implements<IUiTableSource>(address))
        {
            var invalid = basis with
            {
                Version = basis.Version + 1,
                Rows = [],
                Total = 0,
                Status = "error",
                Error = $"Source '{sourceId}' is not a valid IUiTableSource in this brain.",
            };
            await Save(invalid, Changed(invalid));
            return invalid;
        }

        try
        {
            var page = await Get<IUiTableSource>(address).Query(new UiTableQuery(basis.Filter, basis.SortBy, basis.Descending, basis.Page, basis.PageSize));
            var next = basis with
            {
                Version = basis.Version + 1,
                Title = string.IsNullOrWhiteSpace(page.Title) ? basis.Title : page.Title,
                Columns = page.Columns is { Count: > 0 } cols ? [.. cols] : basis.Columns,
                Rows = page.Rows.Select(static r => r.ToArray()).ToArray(),
                Total = page.Total,
                Status = page.Rows.Count > 0 ? "ready" : "empty",
                Error = null,
            };
            await Save(next, Changed(next));
            return next;
        }
#pragma warning disable CA1031 // Bound data source failure becomes table error state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            GetLogger().LogSwallowed(ex, "Bound table source query failed.");
            var failed = basis with
            {
                Version = basis.Version + 1,
                Rows = [],
                Total = 0,
                Status = "error",
                Error = ex.Message,
            };
            await Save(failed, Changed(failed));
            return failed;
        }
    }

    private UiTableChanged Changed(TableState state)
        => new(Id.FullKey(), state.Version, state.Filter, state.SortBy, state.Descending, state.Page);
}
