using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Os.Tests.Unit;

public sealed class OsModuleFacts : ModuleOnTestOSFacts<OsModule>
{
    [Fact]
    public void OsModuleHasExpectedId()
    {
        Assert.Equal("os", OsModule.Id);
    }
}
