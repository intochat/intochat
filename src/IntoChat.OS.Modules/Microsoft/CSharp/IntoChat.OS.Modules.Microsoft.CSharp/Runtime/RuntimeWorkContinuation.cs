using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

internal sealed class RuntimeWorkContinuation(IGrainFactory grains) : IWorkContinuation
{
    public Task Acquire(string brainScope, string token)
        => grains.GetGrain<IBrainRuntimeContinuations>(BrainRuntimeKeys.For(brainScope)).Acquire(token);

    public Task Release(string brainScope, string token)
        => grains.GetGrain<IBrainRuntimeContinuations>(BrainRuntimeKeys.For(brainScope)).Release(token);
}
