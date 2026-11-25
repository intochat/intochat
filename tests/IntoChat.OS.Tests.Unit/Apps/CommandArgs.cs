using DigitalBrain.Invocation;
using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Tests;

// The typed signal a view's command carries, as the screen's [On] reaction reads it.
internal static class CommandArgs
{
    public static TSignal Of<TSignal>(Command command) where TSignal : class
    {
        ArgumentNullException.ThrowIfNull(command);
        return (TSignal)RuntimeProtocol.Deserialize(command.Args ?? default, typeof(TSignal))!;
    }
}
