
namespace IntoChat.OS.Modules.Circles.Tests.Unit;

public sealed class CirclesFacts
{
    [Theory]
    [InlineData("friends", true)]
    [InlineData("a-1", true)]
    [InlineData("Friends", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ACircleNameIsLowerCaseLettersDigitsAndHyphens(string? name, bool expected)
        => Assert.Equal(expected, ICircle.IsValidName(name));

    [Fact]
    public void ACircleNameIsAtMostFortyCharacters()
    {
        Assert.True(ICircle.IsValidName(new string('a', 40)));
        Assert.False(ICircle.IsValidName(new string('a', 41)));
    }

    [Fact]
    public void CircleNeuronsAreAddressedUnderTheirBrain()
    {
        Assert.Equal("c1/circle", ICircle.Key("c1"));
        Assert.Equal("c1/chat", ICircleChat.Key("c1"));
        Assert.Equal("b1/circles", ICircleFactory.Key("b1"));
    }

    [Fact]
    public void ACircleRecordCloneIsIndependentOfTheOriginal()
    {
        var record = new CircleRecord { Name = "friends", Owner = "acct", Members = ["acct"], Version = 3 };

        var clone = record.Clone();
        clone.Name = "renamed";
        clone.Version = 4;

        Assert.Equal("friends", record.Name);
        Assert.Equal(3, record.Version);
        Assert.Equal("acct", clone.Owner);
        Assert.Equal(["acct"], clone.Members);
    }

    [Fact]
    public void ABindingIsAValueNamingTheSharersSlot()
    {
        var binding = new CircleBinding("acct", "notes", "tables", "conn-1");

        Assert.Equal(binding, new CircleBinding("acct", "notes", "tables", "conn-1"));
        Assert.NotEqual(binding, binding with { Slot = "other" });
    }

    [Fact]
    public async Task ACircleBirthRefusesANullBrain()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => new CircleBirth(null!, null!).Created(null!, null!, TestContext.Current.CancellationToken));
}
