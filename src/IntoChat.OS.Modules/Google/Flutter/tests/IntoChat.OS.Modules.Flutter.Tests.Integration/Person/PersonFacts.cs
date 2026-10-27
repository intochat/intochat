using IntoChat.OS.Modules.Flutter.Person;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Person;

public sealed class PersonFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesDisplayNameAndAvatar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<IPerson>("owner/ada").Set("Ada", "https://example.com/ada.png");
        var state = await brain.Get<IPerson>("owner/ada").Read();
        Assert.Equal("Ada", state.DisplayName);
        Assert.Equal("https://example.com/ada.png", state.AvatarUrl);
    }
}
