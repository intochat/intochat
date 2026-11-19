using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace DigitalBrain;

/// <summary>
/// A neuron a query table reads its rows from, by address: the table stores its query and asks for one page whenever the query changes,
/// so the rows stay with the source.
/// </summary>
[NeuronAlias("tableSource")]
public interface ITableSource
{
    /// <summary>The columns the source's rows hold.</summary>
    [ReadOnlyCall]
    Task<IReadOnlyList<QueryTableColumn>> Columns();

    /// <summary>One page of the rows matching <paramref name="q"/>, and how many matched in all.</summary>
    /// <param name="q">The filter, sort and page.</param>
    [ReadOnlyCall]
    Task<TablePage> Query(TableQuery q);
}
