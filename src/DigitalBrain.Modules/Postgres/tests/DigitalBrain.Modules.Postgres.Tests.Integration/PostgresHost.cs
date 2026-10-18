[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Postgres.Tests.Integration.PostgresHost))]

namespace DigitalBrain.Modules.Postgres.Tests.Integration;

// The shared host composes Postgres on its "postgres" connection with the stateless test provider standing in for the
// database (PostgresFacts.Compose); the default server facts query it. Facts that name another connection, swap the
// table store, placements or database creator, choose an isolation, or restart keep their own TestBrain
// ([Trait("host", "own")]).
public sealed class PostgresHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => PostgresFacts.Compose(brain);
}
