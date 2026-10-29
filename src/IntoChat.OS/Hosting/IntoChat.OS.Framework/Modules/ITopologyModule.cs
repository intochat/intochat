using DigitalBrain.Runtime.Orleans;

namespace IntoChat.OS.Framework.Modules;

// A module that carries topology implementations beyond the built-in local ones (03-12): the orleans module supplies the
// cloud topology from DigitalBrain.Runtime.Orleans.Azure, so a host without it fails on Topology=cloud naming that package.
public interface ITopologyModule
{
    IReadOnlyList<ITopologyProfile> TopologyProfiles { get; }
}
