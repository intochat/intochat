[assembly: AssemblyFixture(typeof(DigitalBrain.Authority.Tests.Integration.AuthorityHost))]

namespace DigitalBrain.Authority.Tests.Integration;

// The shared host is the default composition; each fact registers fresh people, accounts and scopes through Unique.
// Facts that compose connectors, price lists or storage, set posture, cookie names or HostedRuntimeOptions, host a
// WebApplication over the brain, or restart it keep their own TestBrain ([Trait("host", "own")]).
public sealed class AuthorityHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;

    // A name no other fact in the host uses: lowercase, so it is also a valid username.
    public static string Unique(string name) => $"{name}-{Guid.NewGuid():N}";
}
