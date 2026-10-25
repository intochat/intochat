using IntoChat.OS.Connections.Platform.Connections.Signals;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime;

// Pre-alias bytes name a type by its pre-0.7.0 namespace and its (unchanged) assembly.
public sealed class LegacyTypeNamesFacts
{
    public static TheoryData<string, Type> Moved => new()
    {
        { "IntoChat.OS.Framework.Platform.Connections.Signals.ConnectionConnected,IntoChat.OS.Connections", typeof(ConnectionConnected) },
        { "IntoChat.OS.Framework.Compute.CostKind,IntoChat.OS.Contracts.Compute", typeof(CostKind) },
        { "IntoChat.OS.Framework.IntoChatOSNames,IntoChat.OS.Contracts.Configuration", typeof(IntoChatOSNames) },
    };

    [Theory]
    [MemberData(nameof(Moved))]
    public void AMovedNameReadsAsTheCurrentType(string legacy, Type current)
    {
        Assert.True(new LegacyTypeNames().TryParse(legacy, out var type));
        Assert.Equal(current, type);
    }

    [Theory]
    [InlineData("IntoChat.OS.Framework.BrainKey,IntoChat.OS.Framework")]
    [InlineData("System.String,System.Private.CoreLib")]
    [InlineData("IntoChat.OS.Framework.Nope,IntoChat.OS.Connections")]
    [InlineData("System.Collections.Generic.List`1[[System.String]],System.Private.CoreLib")]
    public void ANameThatDidNotMoveIsLeftToOrleans(string name)
    {
        Assert.False(new LegacyTypeNames().TryParse(name, out _));
        Assert.False(new LegacyTypeNames().TryFormat(typeof(string), out _));
    }
}
