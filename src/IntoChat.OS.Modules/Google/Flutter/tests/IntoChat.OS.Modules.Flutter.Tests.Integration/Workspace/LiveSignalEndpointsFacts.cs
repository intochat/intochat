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

// A legacy os signal of the shape the golden envelope was captured with (the chat's own ChatChanged is a plain [Signal] since
// 06-05b; the legacy envelope still serves the signals of grain publishers).
[GenerateSerializer, Alias("test.live-envelope-chat-changed")]
public sealed record ChatChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;
