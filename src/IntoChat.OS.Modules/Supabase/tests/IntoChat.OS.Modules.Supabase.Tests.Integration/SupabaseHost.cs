using IntoChat.OS.Supabase;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Supabase.Tests.Integration.SupabaseHost))]

namespace IntoChat.OS.Modules.Supabase.Tests.Integration;

// The shared host composes the user-only Supabase module. The fact that selects the backing store (and expects the
// start to fail) keeps its own TestBrain ([Trait("host", "own")]).
public sealed class SupabaseHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<SupabaseModule>();
}
