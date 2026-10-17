using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Jev.Key(brain));
await Compose();
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Compose();
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
        [new UiChildRef(UIVocabulary.TextType, Name("result")), new UiChildRef(UIVocabulary.TextType, Name("timer")), new UiChildRef(UIVocabulary.LayoutType, Name("input"))],
        Extents: [0, 120, 96]), (await main.Read()).Revision);
    await brain.Get<IApp>(Jev.Key(brain)).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", Jev.Key(brain) + "/main"), Views: [new("full", new("layout", Jev.Key(brain) + "/main"))]));
}
