using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;

using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Framework.Integrations.Accounts;

public static class ScopedAccounts
{
    public static IntegrationAccount[] Visible(BrainScope scope, IntegrationAccount[] records)
        => [.. records.Where(record => record.WorkspaceId == scope.Name || record.WorkspaceId == scope.Id).DistinctBy(record => record.Id)];
}
