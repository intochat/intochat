using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Framework.Platform.Shipping;

// Implemented by modules for host-registered content sources. Platform resolves each named
// publisher account and owns the startup identity its content is published under.
public interface IShippedContentPublisher
{
    string[] Accounts { get; }
    Task PublishAsync(PublisherBrain publisher, CancellationToken cancellationToken);
}
