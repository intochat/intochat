using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Kernel.Composition.Capabilities;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// The silo validates every composed module's Provides against the registry before it starts; the kernel's own http@1
// connector (HttpModule, "web") must be known there or the kernel refuses to boot.
public sealed class KernelCapabilitiesFacts
{
    [Fact]
    public void TheKernelRegistryKnowsHttp()
    {
        var registry = new CapabilityRegistry();
        KernelCapabilities.Configure(registry);
        Assert.True(registry.Known(Capability.Http.V1));
        Assert.Equal(typeof(IHttpRead), registry.ContractOf(Capability.Http.V1));
        Assert.False(registry.IsSingle(Capability.Http.V1));
    }

    [Fact]
    public void ConfiguringTwiceIsIdempotent()
    {
        var registry = new CapabilityRegistry();
        KernelCapabilities.Configure(registry);
        KernelCapabilities.Configure(registry);
        Assert.True(registry.Known(Capability.Http.V1));
    }
}
