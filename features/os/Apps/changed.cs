using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Text.Signals;

// The shown app was installed, updated or removed: show the view again, so its status and actions follow.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var scope = Apps.Scope(brain);
var shown = Sources.Named<IText, TextChanged>(Apps.ViewState(brain), async (_, ct) =>
{
    var target = AppsNames.Parse((await Apps.ReadState(Apps.ViewState(brain))).Target);
    var name = target.Installed ?? target.App;
    return name is null ? [] : [brain.Get<IApp>(IApp.Key(scope, name))];
});
await brain.React<AppChanged>(shown, _ => Apps.Show(brain), brain.Stopping);
