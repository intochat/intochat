namespace IntoChat.Aspire.Bridge;

internal interface IAspireFeed
{
    Task Changed(AspireResourceChanged[] changes);
}
