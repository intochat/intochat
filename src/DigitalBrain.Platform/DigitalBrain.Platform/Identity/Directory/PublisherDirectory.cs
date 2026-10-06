using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using Orleans;

namespace DigitalBrain.Platform.Identity.Directory;

internal sealed class PublisherDirectory(IGrainFactory grains) : IPublisherDirectory
{
    public async Task<PublisherBrain> Resolve(string username, CancellationToken ct)
    {
        var member = await grains.GetGrain<IPrincipal>(username).DefaultMembership().WaitAsync(ct)
            ?? throw new InvalidOperationException($"Publisher account '{username}' does not exist; seed it under DigitalBrain:Auth:Accounts.");
        return new(username, member.AccountId, member.BrainId);
    }
}
