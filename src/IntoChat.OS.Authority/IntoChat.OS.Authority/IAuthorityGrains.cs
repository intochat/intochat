using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority;

// Transitional (06-06c, D-04): the only typed path from Authority to its allow-listed raw grains (NeuronsStillGrains.json).
// The kernel composition implements it and refuses any interface no allow-listed grain implements, so Authority holds no
// grain factory. The grains move in Phases 5/9/10 or go in Phase 12. Connections are a separate seam: 06-08 repoints
// them in the composition when the connection base goes plain (R4-24), without touching Authority.
public interface IAuthorityGrains
{
    T Get<T>(string key) where T : IGrainWithStringKey;

    // The brain neuron of brainId (06-08: a plain neuron of the kernel composition, keyed {brain}/main), reached as the host.
    IBrain Brain(string brainId);

    IConnection Connection(string connectionKey);

    IConnectionInbound ConnectionInbound(string connectionKey);

    // A caller-supplied key resolves through the manifest check, never straight to a grain type.
    T CallerConnection<T>(IServiceProvider services, string connectionKey) where T : class;
}
