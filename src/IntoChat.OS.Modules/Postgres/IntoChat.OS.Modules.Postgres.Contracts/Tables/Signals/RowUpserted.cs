namespace IntoChat.OS.Modules.Postgres.Tables.Signals;

[GenerateSerializer, Alias("postgres.table.upserted")]
public sealed record RowUpserted([property: Id(0)] string Table, [property: Id(1)] TableValue[] Key) : Signal;
