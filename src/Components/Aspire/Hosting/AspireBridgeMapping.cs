using Aspire.Hosting.ApplicationModel;

namespace IntoChat.Aspire.Hosting;

internal static class AspireBridgeMapping
{
    public static BridgeSnapshot From(ResourceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var snapshot = e.Snapshot;
        var parent = snapshot.Relationships
            .FirstOrDefault(static r => string.Equals(r.Type, "Parent", StringComparison.OrdinalIgnoreCase))
            ?.ResourceName;

        var urls = snapshot.Urls
            .Where(static u => !u.IsInternal && !string.IsNullOrWhiteSpace(u.Url))
            .Select(static u => u.Url)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var commands = snapshot.Commands
            .Where(static c =>
                c.State != ResourceCommandState.Hidden
                && c.Visibility != ResourceCommandVisibility.None
                && (c.Arguments is null || c.Arguments.Count == 0))
            .Select(static c => new AspireCommand(
                c.Name,
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Name : c.DisplayName,
                string.IsNullOrWhiteSpace(c.DisplayDescription) ? null : c.DisplayDescription,
                c.State == ResourceCommandState.Enabled,
                string.IsNullOrWhiteSpace(c.ConfirmationMessage) ? null : c.ConfirmationMessage))
            .ToArray();

        return new BridgeSnapshot(
            Name: e.Resource.Name,
            InstanceId: e.ResourceId,
            Type: snapshot.ResourceType ?? "",
            Parent: parent,
            State: snapshot.State?.Text ?? "",
            Health: snapshot.HealthStatus?.ToString(),
            Urls: urls,
            StartedAt: snapshot.StartTimeStamp.HasValue ? new DateTimeOffset(snapshot.StartTimeStamp.Value, TimeSpan.Zero) : null,
            StoppedAt: snapshot.StopTimeStamp.HasValue ? new DateTimeOffset(snapshot.StopTimeStamp.Value, TimeSpan.Zero) : null,
            Commands: commands);
    }
}
