using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps;

// The unit sources the OS ships (src/IntoChat.OS.Apps/Units/<Unit>): embedded as files, deployed through the synapse studio like
// any authored unit, with a headless app record whose activity says what the install waits on. A shipped unit asks for no slots;
// its reactions and view are needs the person consents to, through the normal approvals: the OS never approves them.
public static class ShippedUnits
{
    private const string Prefix = "IntoChat.OS.Apps.Units.";

    public static IReadOnlyList<string> All { get; } =
    [
        "watch.x.elonmusk",
        "triage.github.issues",
        "digest.hn.top",
        "organizer.work.notes",
        "fx.rates.monitor",
    ];

    public static IReadOnlyList<string> Legacy { get; } =
    [
        "circleroom",
        "watch.web",
        "brief.daily",
        "data.csv",
        "relay.webhook",
    ];

    public static IReadOnlyList<string> Interactive { get; } =
    [
        "helloworld",
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuiltInInteractiveFiles =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["helloworld"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HelloWorld.cs"] = """
                    using DigitalBrain;
                    using DigitalBrain.UI;

                    namespace Units.HelloWorld;

                    [Signal("helloworld.toggled")]
                    public sealed record HelloToggled(bool Visible, int Clicks) : Signal;

                    [State("helloworld")]
                    public sealed record HelloWorldState(
                        bool Visible = false,
                        int Clicks = 0)
                    {
                        public HelloWorldState() : this(false, 0) { }
                    }

                    public static class HelloWorldUnit
                    {
                        private const string UnitAlias = "helloworld";
                        private const string ToggleSignal = "helloworld.toggle";

                        [Synapse("helloworld.toggle", "helloworld")]
                        public static Reaction<HelloWorldState> Toggle(SignalEnvelope envelope, HelloWorldState state)
                        {
                            var nextVisible = !state.Visible;
                            var nextClicks = state.Clicks + 1;
                            return Reaction<HelloWorldState>.Of(
                                state with { Visible = nextVisible, Clicks = nextClicks },
                                new HelloToggled(nextVisible, nextClicks));
                        }

                        [Synapse("helloworld")]
                        public static View View(HelloWorldState state) =>
                            new("Hello World", new Surface(
                            [
                                new Row(
                                [
                                    new Badge(state.Visible ? "Greeting Visible" : "Ready", state.Visible ? "success" : "neutral"),
                                    new StatCardNode("clicks", "Clicks", state.Clicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                ]),
                                new Text(state.Visible ? "Hello, IntoChat OS!" : "Click the button below"),
                                new Button(
                                    Key: "toggle-hello",
                                    Label: state.Visible ? "Hide Hello" : "Say Hello",
                                    Command: new Command(".", ToggleSignal),
                                    Tone: ButtonTone.Accent)
                                {
                                    Style = Mix.Style()
                                        .Tone(UiTone.Accent)
                                        .Padding(16, 10)
                                        .Radius(10)
                                        .Motion(UiMotion.Spring)
                                        .OnPress(static s => s.Scale(0.98)),
                                },
                            ], Title: "Hello World", Subtitle: "Interactive Micro-App")
                            {
                                Style = Mix.Style()
                                    .Tone(UiTone.Card)
                                    .Radius(14)
                                    .Elevation(UiElevation.Subtle),
                            });
                    }
                    """,
                ["spec.md"] = """
                    # Hello World

                    Interactive micro-app where clicking the toggle button (`toggle-hello`) reveals and hides `"Hello, IntoChat OS!"` with zero external capability slots.
                    """,
                ["scenarios.json"] = """
                    [
                      {
                        "name": "toggles-hello-visibility-on-and-off",
                        "given": {},
                        "when": [
                          "then view helloworld contains \"Click the button below\"",
                          "then view helloworld contains \"Say Hello\"",
                          "publish helloworld.toggle {}",
                          "then signal helloworld.toggled",
                          "then state helloworld Visible = true",
                          "then state helloworld Clicks = 1",
                          "then view helloworld contains \"Hello, IntoChat OS!\"",
                          "then view helloworld contains \"Hide Hello\"",
                          "publish helloworld.toggle {}"
                        ],
                        "then": [
                          "state helloworld Visible = false",
                          "state helloworld Clicks = 2",
                          "view helloworld contains \"Click the button below\"",
                          "view helloworld contains \"Say Hello\""
                        ]
                      }
                    ]
                    """,
                ["SKILL.md"] = """
                    ---
                    name: helloworld
                    title: Hello World
                    description: Interactive micro-app with a toggle button ("Say Hello" / "Hide Hello") that reveals and hides "Hello, IntoChat OS!".
                    capabilities: []
                    schedule: none
                    ---

                    # Hello World (`helloworld`)

                    Interactive UI micro-app requiring zero external connectors or secrets.
                    """,
            },
        };

    public static bool IsKnown(string alias)
        => All.Contains(alias, StringComparer.Ordinal)
        || Legacy.Contains(alias, StringComparer.Ordinal)
        || Interactive.Contains(alias, StringComparer.Ordinal);

    // A shipped unit's files keyed by path: its .cs source, spec.md and scenarios.json. Empty for an alias the OS does not ship.
    public static IReadOnlyDictionary<string, string> Files(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var assembly = typeof(ShippedUnits).Assembly;
        var prefix = Prefix + alias + "/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded.");
            using var reader = new StreamReader(stream);
            files[name[prefix.Length..]] = reader.ReadToEnd();
        }
        if (files.Count == 0 && BuiltInInteractiveFiles.TryGetValue(alias, out var interactive))
        {
            return new Dictionary<string, string>(interactive, StringComparer.Ordinal);
        }
        return files;
    }

    // Deploys alias's shipped files and leaves the install as the host answers it, Pending until the person approves. The first
    // deploy records the unit as a headless app and begins its build. While the record is Building (waiting for approval, or a
    // crash between Create and the end of the build), every deploy ends the build by the install the studio reports: Pending keeps
    // it waiting, installed (Unchanged once the person approved) makes it Live. So the connect after an approval finishes the build,
    // and a connect after a crash deploys again (the studio deploys anew when the installs neuron knows nothing of the alias).
    // A Live (or otherwise settled) record only deploys again: the same files deploy once.
    public static async Task<UnitDeployResult> Deploy(ISynapseStudio studio, IApp app, string alias, string title)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(app);
        var files = Files(alias);
        if (files.Count == 0) { throw new ArgumentException($"The OS ships no unit {alias}.", nameof(alias)); }
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var record = await app.Read();
        if (record.Manifest.Title.Length == 0)
        {
            await app.Create(new AppManifest(title, files.TryGetValue("spec.md", out var spec) ? FirstLine(spec) : title, [], [], [], Headless: true), spec ?? "");
            await app.BeginBuild("shipped unit " + alias);
        }
        // Drafted: a crash between Create and BeginBuild; its build begins now.
        else if (record.Status == AppStatus.Drafted) { await app.BeginBuild("shipped unit " + alias); }
        else if (record.Status != AppStatus.Building) { return await studio.DeployFiles(alias, files, none); }
        var attempt = await app.CurrentBuildAttempt();
        var result = await studio.DeployFiles(alias, files, none);
        if (!result.Ok) { await app.CompleteBuild(attempt, null, result.Text); return result; }
        var install = result.Installs.TryGetValue(alias, out var own) ? own : new DigitalBrain.Units.InstallOutcome(DigitalBrain.Units.InstallStatus.Unchanged, "", null);
        if (install.Status is DigitalBrain.Units.InstallStatus.Refused or DigitalBrain.Units.InstallStatus.Denied)
        { await app.CompleteBuild(attempt, null, $"{alias} was not installed: {install.Reason ?? install.Status.ToString()}"); }
        else { await app.CompleteUnitBuild(attempt, result.Hash ?? "", install.Status, install.Sentence); }
        return result;
    }

    // A shipped unit's title: its spec's first line ("# Circle room"), or the alias when it has none.
    public static string Title(string alias)
        => Files(alias).TryGetValue("spec.md", out var spec) && FirstLine(spec) is { Length: > 0 } title ? title : alias;

    private static string FirstLine(string text)
        => text.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
