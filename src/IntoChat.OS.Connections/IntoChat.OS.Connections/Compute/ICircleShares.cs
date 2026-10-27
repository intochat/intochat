using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Connections.Compute;

// Only the circle neuron matching the key may configure its owner's share.
// Authority derives the owner from the kernel rather than accepting a supplied account.
[Alias("platform.circle-shares")]
public interface ICircleShares : IGrainWithStringKey
{
    Task Configure(int percent, string[] members);
}
