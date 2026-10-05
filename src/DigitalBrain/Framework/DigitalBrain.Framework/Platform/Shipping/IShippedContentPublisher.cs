namespace DigitalBrain.Framework.Platform.Shipping;

// Implemented by modules for host-registered content sources. Platform owns their startup identity.
public interface IShippedContentPublisher
{
    string[] Publishers { get; }
    Task PublishAsync(string publisher, CancellationToken cancellationToken);
}
