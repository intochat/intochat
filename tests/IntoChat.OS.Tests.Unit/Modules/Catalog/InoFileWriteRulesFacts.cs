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

    private static DigitalBrain.Capabilities.Storage.Item Root(string id, string name) =>
        new(new DigitalBrain.Capabilities.Storage.ItemId(id), name, DigitalBrain.Capabilities.Storage.ItemKind.Folder, null, null,
            DigitalBrain.Capabilities.Storage.ItemFlags.Read | DigitalBrain.Capabilities.Storage.ItemFlags.Write);

    [Fact]
    public void TheDocumentsRootWinsOverAnOlderFirstRoot()
    {
        var target = InoFileWriteRules.Target([Root("r1", "IntoChat.OS.Kernel"), Root("r2", "Documents")], @"C:\Users\someone\Documents");
        Assert.Equal("r2", target!.Id.Value);
    }

    [Fact]
    public void WithoutADocumentsRootTheFirstWritableRootIsUsed()
        => Assert.Equal("r1", InoFileWriteRules.Target([Root("r1", "Work")], @"C:\Users\someone\Documents")!.Id.Value);

    [Fact]
    public void ContentOverTheLimitIsRefused()
        => Assert.NotNull(InoFileWriteRules.Check("big.txt", new string('x', InoFileWriteRules.MaxContentCharacters + 1)));
}
