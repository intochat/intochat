using IntoChat.OS.Modules.Apps;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoFileWriteRulesFacts
{
    [Theory]
    [InlineData("hello.txt")]
    [InlineData("notes 2026-11-17.md")]
    public void AFileNameInTheRootIsAccepted(string name) => Assert.Null(InoFileWriteRules.Check(name, "hi"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("sub/hello.txt")]
    [InlineData(@"..\hello.txt")]
    [InlineData("C:hello.txt")]
    [InlineData("..")]
    [InlineData("a|b.txt")]
    public void AnythingButAPlainFileNameIsRefused(string? name) => Assert.NotNull(InoFileWriteRules.Check(name, "hi"));

    [Fact]
    public void ContentOverTheLimitIsRefused()
        => Assert.NotNull(InoFileWriteRules.Check("big.txt", new string('x', InoFileWriteRules.MaxContentCharacters + 1)));
}
