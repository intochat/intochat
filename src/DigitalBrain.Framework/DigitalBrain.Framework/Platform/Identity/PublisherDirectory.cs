
namespace DigitalBrain.Framework.Platform.Identity;

// A publisher is an ordinary account; what it ships lives in that account's default brain.
public sealed record PublisherBrain(string Username, string AccountId, string BrainId)
{
    public string ScopeId => BrainScope.Create(AccountId, BrainId).Id;
}

public interface IPublisherDirectory
{
    Task<PublisherBrain> Resolve(string username, CancellationToken ct);
}
