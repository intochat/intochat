using DigitalBrain.Kernel.Enforcement;

using DigitalBrain.Kernel.Platform.Integrations.Accounts;

namespace DigitalBrain.Kernel.Integrations.Accounts;

public static class ScopedAccounts
{
    public static IntegrationAccount[] Visible(BrainScope scope, IntegrationAccount[] records)
        => [.. records.Where(record => record.WorkspaceId == scope.Name || record.WorkspaceId == scope.Id).DistinctBy(record => record.Id)];
}
