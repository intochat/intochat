using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// "open" composes the Jev console (the answer, a prompt and a Send button) and opens its window;
// input "docked" composes it without the window. The parts are edge neurons, so composing is idempotent.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Jev.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Compose();
    if (request.Input != "docked") { await Jev.OpenWindow(brain, "window", "Jev", "surface"); }
}

string Name(string part) => Jev.Name(brain, part);

async Task Compose()
{
    await brain.Get<ITextField>(Name("prompt")).Configure("Ask Jev to do something…", "multiline", Name("send"));
    await brain.Get<IButton>(Name("send")).Set("Send", "ask");
    var input = brain.Get<ILayout>(Name("input"));
    await input.Set(new LayoutDefinition("row",
        [new UiChildRef(UIVocabulary.TextFieldType, Name("prompt")), new UiChildRef(UIVocabulary.ButtonType, Name("send"))],
        Extents: [0, 90]), (await input.Read()).Revision);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column",
        [new UiChildRef(UIVocabulary.TextType, Name("result")), new UiChildRef(UIVocabulary.LayoutType, Name("input"))],
        Extents: [0, 96]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Jev", [new UiChildRef(UIVocabulary.LayoutType, Name("main"))]), (await surface.Read()).Revision);
}
