[assembly: AssemblyFixture(typeof(IntoChat.OS.Authority.Tests.Integration.AuthorityHost))]

namespace IntoChat.OS.Authority.Tests.Integration;

// The shared host is the default composition; each fact registers fresh people, accounts and scopes through Unique.
// Facts that compose connectors, price lists or storage, set posture or cookie names, host a
// WebApplication over the brain, or restart it keep their own TestBrain ([Trait("host", "own")]).
public sealed class AuthorityHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;

    // A name no other fact in the host uses: lowercase, so it is also a valid username.
    public static string Unique(string name) => $"{name}-{Guid.NewGuid():N}";
}
