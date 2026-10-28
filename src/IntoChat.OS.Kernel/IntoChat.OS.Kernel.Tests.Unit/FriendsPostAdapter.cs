using DigitalBrain.Spi;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

// Friends.Post is the script edge (IntoChatOSConnection) and the in-process replay (ITestBrain). The shared body takes INeuronProxies.
public static partial class Friends
{
    public static Task Post(ITestBrain brain, string scope, Guid requestId, string text)
        => Post(brain.Services.GetRequiredService<INeuronProxies>(), scope, requestId, text);
}
