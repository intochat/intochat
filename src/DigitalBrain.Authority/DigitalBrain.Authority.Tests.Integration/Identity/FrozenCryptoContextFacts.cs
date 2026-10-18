using DigitalBrain.Authority.Identity.Directory;
using DigitalBrain.Authority.Secrets;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

// These bytes are HKDF/proof domain separators. Changing them makes sealed owner keys and stored
// deployment markers unreadable. The expectations are hex so no token rename can alter them.
public sealed class FrozenCryptoContextFacts
{
    [Fact]
    public void OwnerKeysContextBytesAreFrozen() =>
        Assert.Equal("4469676974616C427261696E2E4F776E65724B6579732E7633",
            Convert.ToHexString(MasterKeyWrapper.OwnerKeysContext));

    [Fact]
    public void KeyBindingContextBytesAreFrozen() =>
        Assert.Equal("4469676974616C427261696E206465706C6F796D656E74206B65792062696E64696E67207631",
            Convert.ToHexString(DeploymentStorageIdentity.KeyBindingContext));
}
