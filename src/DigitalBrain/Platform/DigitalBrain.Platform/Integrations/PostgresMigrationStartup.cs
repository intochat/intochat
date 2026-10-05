using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Enforcement;
using DigitalBrain.Postgres;

namespace DigitalBrain.Platform.Integrations;

internal sealed class PostgresMigrationStartup(IGrainFactory grains, ModuleInventory inventory) : IIntegrationStartup
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!inventory.Types.Any(t => t.FullName == "DigitalBrain.Postgres.PostgresModule")) { return; }
        using var _ = CallerContextScope.Scoped(new CallerContext
        {
            PrincipalId = "platform",
            AccountId = "platform",
            BrainId = "migration",
            Kind = CallerKind.Platform,
            StampedBy = TrustedEdge.Platform,
        });
        await grains.GetGrain<IPostgresStorageMigration>("table-owners-v1").Ensure().WaitAsync(cancellationToken);
    }
}
