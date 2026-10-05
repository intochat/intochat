using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Enforcement;
using Orleans.Metadata;
using Xunit;

namespace DigitalBrain.Framework.Tests.Unit;

// A [GenerateSerializer] type without [Alias] is identified by its full type name, so a
// namespace move silently changes its wire and storage identity. These four were renamed in
// the Kernel -> Framework restructuring; their aliases pin the identity old payloads carry.
public sealed class SerializedIdentityFacts
{
    [Theory]
    [InlineData(typeof(DocumentState), "DigitalBrain.Kernel.DocumentState")]
    [InlineData(typeof(DocumentIndexState), "DigitalBrain.Kernel.DocumentIndexState")]
    [InlineData(typeof(CapacityUnavailableException), "DigitalBrain.Kernel.Capacity.CapacityUnavailableException")]
    [InlineData(typeof(UntrustedCallerException), "DigitalBrain.Kernel.Enforcement.UntrustedCallerException")]
    public void RenamedSerializedTypesKeepTheirPreRenameIdentity(Type type, string alias)
    {
        var attribute = Assert.Single(type.GetCustomAttributes(typeof(AliasAttribute), inherit: false).Cast<AliasAttribute>());
        Assert.Equal(alias, attribute.Alias);
    }
}
