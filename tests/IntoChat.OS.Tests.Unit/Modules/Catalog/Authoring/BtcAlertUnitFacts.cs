using System.Text.Json;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

public sealed class BtcAlertUnitFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SynapseCompiler HostCompiler = new(ReferenceSet.FromDefaultContext());

    private static string LocateRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "btcalert", "BtcAlert.cs");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate apps/btcalert/BtcAlert.cs from test base directory.");
    }

    [Fact]
    public async Task BtcAlertThreeFileReferenceFolderCompilesPassesBoundaryAndRunsAllScenarios()
    {
        var repoRoot = LocateRepoRoot();
        var appDir = Path.Combine(repoRoot, "apps", "btcalert");

        var codePath = Path.Combine(appDir, "BtcAlert.cs");
        var scenariosPath = Path.Combine(appDir, "scenarios.json");
        var skillPath = Path.Combine(appDir, "SKILL.md");

        Assert.True(File.Exists(codePath), "BtcAlert.cs must exist");
        Assert.True(File.Exists(scenariosPath), "scenarios.json must exist");
        Assert.True(File.Exists(skillPath), "SKILL.md must exist");

        var code = await File.ReadAllTextAsync(codePath, Ct);
        var scenariosJson = await File.ReadAllTextAsync(scenariosPath, Ct);
        var skillMd = await File.ReadAllTextAsync(skillPath, Ct);

        Assert.Contains("Asks<IHttpRead>", code, StringComparison.Ordinal);
        Assert.Contains("Asks<INotifications>", code, StringComparison.Ordinal);
        Assert.Contains("Every = \"every 1m\"", code, StringComparison.Ordinal);
        Assert.Contains("btcalert", skillMd, StringComparison.Ordinal);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BtcAlert.cs"] = code,
            [UnitFiles.Scenarios] = scenariosJson,
            ["SKILL.md"] = skillMd,
        };

        var source = new SynapseSource("btcalert", UnitFiles.Code(files), null);
        var build = HostCompiler.Compile(source);
        Assert.True(build.Success, string.Join("\n", build.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}")));
        Assert.Null(SynapseCompiler.Verify(build));

        var scenarios = JsonSerializer.Deserialize<Scenario[]>(scenariosJson, JsonSerializerOptions.Web)!;
        Assert.Equal(2, scenarios.Length);

        var needs = new UnitNeeds(
            [
                new AuthoredSlotNeed("market", "http", ["api.coinbase.com"]),
                new AuthoredSlotNeed("notify", "notifications", []),
            ],
            Schedule: "every 1m",
            View: true);

        var check = await BuilderTools.Check(HostCompiler, "btcalert", files, needs, scenarios, Ct);
        Assert.True(
            check.Passed,
            string.Join("\n", check.Diagnostics.Select(d => $"{d.Id}: {d.Message}").Concat(check.Failures)));

        var transcript = await IntoChat.OS.Modules.Apps.Debugger.ScenarioTranscripts.Run("btcalert", files, null, Ct);
        Assert.Contains("✓ alerts-once-on-threshold-drop-and-validates-rules", transcript, StringComparison.Ordinal);
        Assert.Contains("✓ stays-armed-when-spot-is-above-threshold", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("✗", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatorAcceptsNotificationsAndStorageSlotCapabilities()
    {
        var authoredJson = """
            {
              "alias": "btc.watch",
              "title": "BTC Watch",
              "spec": "Watches BTC price and sends notifications.",
              "state": { "LastPrice": "double", "Armed": "bool" },
              "signals": [{ "alias": "btc.dropped", "fields": { "Price": "double" } }],
              "needs": {
                "slots": [
                  { "name": "market", "capability": "http@1", "hosts": ["api.coinbase.com"] },
                  { "name": "notify", "capability": "notifications", "hosts": [] },
                  { "name": "vault", "capability": "storage@1", "hosts": [] }
                ],
                "schedule": "every 1m",
                "view": true
              },
              "scenarios": [
                {
                  "name": "smoke",
                  "given": {
                    "http": { "market": { "*": { "price": 84000 } } },
                    "notifications": { "notify": { "*": { "id": "n1" } } },
                    "storage": { "vault": { "config.json": "{}" } }
                  },
                  "when": ["tick 1m"],
                  "then": ["signal btc.dropped"]
                }
              ]
            }
            """;

        var unit = Creator.Parse<AuthoredUnit>(authoredJson);
        Assert.Equal(3, unit.Needs.Slots.Count);
        var parsed = ScenarioSteps.Parse(unit.Scenarios[0]);
        Assert.Equal(["market", "notify", "vault"], parsed.Fakes.Slots);
        await Task.CompletedTask;
    }
}
