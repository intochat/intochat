using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Platform.Integrations.Accounts;

public static class ScopedConnections
{
    public static Connection[] Visible(BrainScope scope, Connection[] records)
        => [.. records.Where(record => record.WorkspaceId == scope.Id).DistinctBy(record => record.Id)];
}
