using DigitalBrain.Capabilities;

namespace DigitalBrain.Tests.Unit;

public sealed class CapabilityIdFacts
{
    [Fact]
    public void Parse_reads_name_at_major()
    {
        Assert.Equal(new CapabilityId("storage", 1), CapabilityId.Parse("storage@1"));
    }

    [Fact]
    public void ToString_round_trips()
    {
        var id = new CapabilityId("mail-send", 12);
        Assert.Equal("mail-send@12", id.ToString());
        Assert.Equal(id, CapabilityId.Parse(id.ToString()));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("storage@x")]
    [InlineData("@1")]
    [InlineData("Storage@1")]
    [InlineData("storage@-1")]
    [InlineData("")]
    public void Parse_rejects_malformed_text(string text)
    {
        Assert.Throws<FormatException>(() => CapabilityId.Parse(text));
        Assert.False(CapabilityId.TryParse(text, out _));
    }

    [Fact]
    public void Constructor_rejects_bad_names()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityId("Bad Name", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityId("ok", -1));
    }
}
