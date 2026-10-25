namespace IntoChat.OS.Modules.Postgres.Tables;

[GenerateSerializer, Alias("postgres.table.column")]
public sealed record TableColumn([property: Id(0)] string Name, [property: Id(1)] string Type);

[GenerateSerializer, Alias("postgres.table.definition")]
public sealed record TableDefinition([property: Id(0)] TableColumn[] Columns, [property: Id(1)] string[] PrimaryKey);

// Json is a single JSON value, including quotes for text. Null is allowed outside the key.
[GenerateSerializer, Alias("postgres.table.value")]
public sealed record TableValue([property: Id(0)] string Column, [property: Id(1)] string Json);

[GenerateSerializer, Alias("postgres.table.accepted")]
public sealed record AcceptedTable([property: Id(0)] string Table, [property: Id(1)] TableDefinition Definition, [property: Id(2)] long Revision);
