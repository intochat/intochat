using System.Collections.Frozen;
using System.Reflection;
using DigitalBrain.State;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority.Storage;

// Default-deny export: a state is exported only when its type is marked [ExportableState] and its state
// name is not on the permanent hard-deny list. The hard-deny names win over any attribute.
internal sealed class StateExportPolicy
{
    internal static readonly FrozenSet<string> HardDenied = new[]
    {
        "vault", "credential", "registration", "access", "account", "principal", "reminders",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static StateExportPolicy Default { get; } = new();

    internal bool Allows(string stateName, Type stateType)
        => !HardDenied.Contains(stateName)
            && stateType.GetCustomAttribute<ExportableStateAttribute>(inherit: false) is not null
            && stateType.Assembly != typeof(StateExportPolicy).Assembly
            && !typeof(ConnectionState).IsAssignableFrom(stateType);
}
