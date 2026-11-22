using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Postgres.Tables;

// Use a unique grain key per installed table. First Define binds it permanently to the
// stamped brain and app; every subsequent operation requires that same caller scope.
// Foreign callers are admitted through per-resource IOffers ([Offered]): Viewers may Read/Page,
// Editors may also Upsert/Delete; Define stays owner-only.
[Shareable, Offered]
[NeuronAlias("postgres.table"), DefaultNeuronType("postgres.table")]
public interface ITable
{
    Task<AcceptedTable> Define(TableDefinition definition);
    [Shared] Task<bool> Upsert(TableValue[] key, TableValue[] values);
    [Shared] Task<bool> Delete(TableValue[] key);
    [Shared, ReadOnlyCall] Task<TableValue[]?> Read(TableValue[] key);
    [Shared, ReadOnlyCall] Task<TableValue[][]> Page(int offset = 0, int limit = 200);
}
