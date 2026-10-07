using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpFileKeysFacts
{
    [Fact]
    public void AKeyIsTheBrainScopeTheCSharpSegmentAndTheName()
        => Assert.Equal("alpha/csharp/report", CSharpFileKeys.For("alpha", "report"));

    [Theory]
    [InlineData("../other")]
    [InlineData("other/program")]
    [InlineData("C:\\private")]
    [InlineData("")]
    [InlineData("catalog")]
    public void ANameThatCouldReachAnotherKeyIsRefused(string name)
        => Assert.Throws<ArgumentException>(() => CSharpFileKeys.For("alpha", name));

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a\\b")]
    public void AScopeThatIsNotABrainIdIsRefused(string scope)
        => Assert.Throws<ArgumentException>(() => CSharpFileKeys.For(scope, "report"));

    [Fact]
    public void EveryKeyBelongsToItsBrainForAccessChecks()
        => Assert.Equal("alpha", NeuronAccess.ForBrainKey(CSharpFileKeys.For("alpha", "report")).Scope);

    // ForBrainKey splits a key at '/' or ':', so Contracts refuses ':' in a scope even though a brain id may
    // contain it; what Contracts accepts must always be a valid brain id.
    [Theory]
    [InlineData("personal")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("")]
    public void ContractsAcceptNoScopeTheFrameworkRefuses(string scope)
    {
        if (Record.Exception(() => CSharpFileKeys.For(scope, "x")) is null) { Assert.True(BrainScope.IsValidId(scope)); }
    }
}
