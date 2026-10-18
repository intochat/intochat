using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

public abstract class IntoChatOSModuleProjection
{
    public abstract void Apply<TResource>(IResourceBuilder<TResource> builder)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints;
}
