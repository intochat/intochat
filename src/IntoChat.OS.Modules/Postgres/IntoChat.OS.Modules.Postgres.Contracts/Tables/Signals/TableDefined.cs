namespace IntoChat.OS.Postgres.Tables.Signals;

[GenerateSerializer, Alias("postgres.table.defined")]
public sealed record TableDefined([property: Id(0)] string Table, [property: Id(1)] long Revision) : Signal;
