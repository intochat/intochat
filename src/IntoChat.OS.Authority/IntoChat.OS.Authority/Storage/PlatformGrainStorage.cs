using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Storage;

// Grain storage that keeps every state of the given scope in that scope's journal.
public static class PlatformGrainStorage
{
    public static IGrainStorage ForScope(IServiceProvider services, string scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return new BrainGrainStorage(services.GetRequiredService<BrainLogs>(), services.GetRequiredService<Serializer>(), scope);
    }
}
