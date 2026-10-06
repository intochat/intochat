namespace DigitalBrain.Supabase;

// Positional: the n-th parameter binds $n. Value is text; PostgreSQL casts it where the SQL says so.
[GenerateSerializer, Alias("db.supabase.parameter")]
public sealed record SupabaseParameter([property: Id(0)] string Name, [property: Id(1)] string? Value);
