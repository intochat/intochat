namespace IntoChat.OS.Aspire.Hosting;

public sealed class IntoChatOSClientReference
{
    internal IntoChatOSClientReference(IntoChatOSBuilder brain, bool shareHttpIdentity = false)
    { Brain = brain; ShareHttpIdentity = shareHttpIdentity; }

    internal bool ShareHttpIdentity { get; }

    internal IntoChatOSBuilder Brain { get; }
}
