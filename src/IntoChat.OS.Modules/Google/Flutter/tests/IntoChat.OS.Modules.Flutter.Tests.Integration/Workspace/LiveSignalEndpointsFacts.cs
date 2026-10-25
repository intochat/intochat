using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

public sealed class LiveSignalEndpointsFacts
{
    // The golden bytes are shared with the Dart mirror's check; a change here is a wire change.
    [Fact]
    public async Task TheEnvelopeShapeIsUnchanged()
    {
        var signal = new ChatChanged("brain/chat", 7) { Publisher = "brain/chat" };
        var golden = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "live-envelope.golden.json"), TestContext.Current.CancellationToken);

        Assert.Equal(golden, LiveSignalEndpoints.Envelope(signal, 42, replay: true));
    }
}
