namespace IntoChat.OS.Postgres;

// Positional: the n-th parameter binds $n. Value is text; PostgreSQL casts it where the SQL says so.
[GenerateSerializer, Alias("db.postgres.parameter")]
public sealed record PostgresParameter([property: Id(0)] string Name, [property: Id(1)] string? Value);
