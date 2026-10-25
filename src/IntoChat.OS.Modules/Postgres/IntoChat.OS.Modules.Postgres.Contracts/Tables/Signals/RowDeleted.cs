namespace IntoChat.OS.Modules.Postgres.Tables.Signals;

[GenerateSerializer, Alias("postgres.table.deleted")]
public sealed record RowDeleted([property: Id(0)] string Table, [property: Id(1)] TableValue[] Key) : Signal;
