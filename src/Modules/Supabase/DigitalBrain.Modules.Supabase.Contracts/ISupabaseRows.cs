using DigitalBrain.Data;

namespace DigitalBrain.Supabase;

[Alias("supabase.rows"), Orleans.Metadata.DefaultGrainType("data.supabase-rows")]
public interface ISupabaseRows : IRowSource
{
}
