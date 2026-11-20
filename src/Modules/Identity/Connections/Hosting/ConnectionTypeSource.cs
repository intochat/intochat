using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Kernel;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Connections.Hosting;

// The connectors a module declares (ConnectorDefinition.DeclaredBy) as compiled type entries.
internal sealed class ConnectionTypeSource : IModuleTypeSource
{
    public IEnumerable<BrainType> TypesOf(OsModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ConnectorDefinition.DeclaredBy(module.Type).Select(connector => Connector(connector, module.Id.ToString()));
    }

    private static BrainType Connector(ConnectorDefinition connector, string provider)
        => new(connector.Id, connector.Id, TypeKind.Connector, provider, connector.DisplayName,
            [.. connector.Neurons.Concat(connector.Signals).Select(type => type.FullName!)], []);
}
