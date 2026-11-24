namespace IntoChat.Aspire.Bridge;

public interface IAspireFeed
{
    Task Changed(AspireResourceChanged[] changes);
}
