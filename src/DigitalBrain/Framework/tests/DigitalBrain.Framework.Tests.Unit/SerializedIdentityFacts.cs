using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using Orleans.Metadata;
using Xunit;

namespace DigitalBrain.Framework.Tests.Unit;

// A [GenerateSerializer] type without [Alias] is identified by its full type name, so a
// namespace move silently changes its wire and storage identity. These aliases pin identities
// across the Kernel -> Framework restructuring and Connector/Connection/Integration renames.
public sealed class SerializedIdentityFacts
{
    [Theory]
    [InlineData(typeof(DocumentState), "DigitalBrain.Kernel.DocumentState")]
    [InlineData(typeof(DocumentIndexState), "DigitalBrain.Kernel.DocumentIndexState")]
    [InlineData(typeof(CapacityUnavailableException), "DigitalBrain.Kernel.Capacity.CapacityUnavailableException")]
    [InlineData(typeof(UntrustedCallerException), "DigitalBrain.Kernel.Enforcement.UntrustedCallerException")]
    [InlineData(typeof(IntegrationStatus), "DigitalBrain.Framework.Platform.Integrations.RegistrationStatus")]
    [InlineData(typeof(ConnectionStatus), "DigitalBrain.Framework.Platform.Integrations.Accounts.AccountStatus")]
    [InlineData(typeof(ConnectionProbeOutcome), "DigitalBrain.Framework.Platform.Integrations.Accounts.AccountProbeOutcome")]
    public void RenamedSerializedTypesKeepTheirPreRenameIdentity(Type type, string alias)
    {
        var attribute = Assert.Single(type.GetCustomAttributes(typeof(AliasAttribute), inherit: false).Cast<AliasAttribute>());
        Assert.Equal(alias, attribute.Alias);
    }
}
