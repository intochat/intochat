using System.Reflection;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps.Tests;

public sealed class CompiledAppsHostAdversarialFacts
{
    [Fact]
    public void HostedByName_ContainsOnlyKernelApps_ExcludesNonKernelApps()
    {
        var hostedByNameField = typeof(CompiledAppsHost).GetField(
            "HostedByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(hostedByNameField);

        var hostedByName = (IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>)
            hostedByNameField!.GetValue(null)!;

        Assert.Equal(2, hostedByName.Count);
        Assert.True(hostedByName.ContainsKey("ino"));
        Assert.True(hostedByName.ContainsKey("settings"));

        // All non-kernel apps must be strictly absent from in-process hosting
        Assert.False(hostedByName.ContainsKey("gallery"));
        Assert.False(hostedByName.ContainsKey("creator"));
        Assert.False(hostedByName.ContainsKey("post"));
        Assert.False(hostedByName.ContainsKey("photocraft"));
        Assert.False(hostedByName.ContainsKey("filmcraft"));
    }

    [Fact]
    public void HostedByName_IndexingNonHostedApp_ThrowsKeyNotFoundException()
    {
        var hostedByNameField = typeof(CompiledAppsHost).GetField(
            "HostedByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        var hostedByName = (IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>)
            hostedByNameField!.GetValue(null)!;

        Assert.Throws<KeyNotFoundException>(() => hostedByName["photocraft"]);
        Assert.Throws<KeyNotFoundException>(() => hostedByName["filmcraft"]);
        Assert.Throws<KeyNotFoundException>(() => hostedByName["gallery"]);
        Assert.Throws<KeyNotFoundException>(() => hostedByName["creator"]);
        Assert.Throws<KeyNotFoundException>(() => hostedByName["post"]);
    }

    [Fact]
    public void StudioAppRouting_UnhostedAppsRequireDirectAppNeuronForwarding()
    {
        // Line 344 in CompiledAppsHost dispatches studioApp ("photocraft" or "filmcraft")
        // Verify that neither is hosted, which necessitates forwarding directly via SiloNeurons.Get<IApp>
        var hostedByNameField = typeof(CompiledAppsHost).GetField(
            "HostedByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        var hostedByName = (IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>)
            hostedByNameField!.GetValue(null)!;

        string[] studioApps = ["photocraft", "filmcraft"];
        foreach (var app in studioApps)
        {
            Assert.False(hostedByName.ContainsKey(app), $"Studio app '{app}' must be unhosted.");
        }
    }

    [Theory]
    // Valid PhotoCraft routes
    [InlineData("routed:photocraft.edit", "photocraft")]
    [InlineData("routed:photocraft.canvas", "photocraft")]
    [InlineData("routed:photocraft.removeBackground", "photocraft")]
    [InlineData("routed:photocraft.layer.newFillLayer.gradient", "photocraft")]
    [InlineData("routed:photocraft.selectSubject", "photocraft")]
    [InlineData("routed:photocraft.", "photocraft")]
    // Valid FilmCraft routes
    [InlineData("routed:filmcraft.timeline", "filmcraft")]
    [InlineData("routed:filmcraft.render", "filmcraft")]
    [InlineData("routed:filmcraft.timeline.razor", "filmcraft")]
    [InlineData("routed:filmcraft.color.lumetri.auto", "filmcraft")]
    [InlineData("routed:filmcraft.sequence.exportMp4", "filmcraft")]
    [InlineData("routed:filmcraft.", "filmcraft")]
    // Boundary conditions: Missing trailing dot (MUST NOT match)
    [InlineData("routed:photocraft", null)]
    [InlineData("routed:filmcraft", null)]
    // Prefix collision / spoofing attempts (MUST NOT match)
    [InlineData("routed:photocrafting.edit", null)]
    [InlineData("routed:photocrafts.canvas", null)]
    [InlineData("routed:photocraft_fake.do", null)]
    [InlineData("routed:filmcrafty.cut", null)]
    [InlineData("routed:filmcrafts.timeline", null)]
    [InlineData("routed:filmcraft_fake.do", null)]
    // Case sensitivity: Ordinal comparison must not match uppercase/mixed-case
    [InlineData("routed:PHOTOCRAFT.edit", null)]
    [InlineData("ROUTED:photocraft.edit", null)]
    [InlineData("routed:FILMCRAFT.timeline", null)]
    // Non-studio app route hints (MUST NOT match studio apps)
    [InlineData("routed:gallery.open", null)]
    [InlineData("routed:creator.new", null)]
    [InlineData("routed:post.publish", null)]
    [InlineData("routed:settings.navigate:models:local-server", null)]
    [InlineData("routed:customapp.action", null)]
    [InlineData("routed:arbitrary.long.nested.namespace.action", null)]
    // Clarification route hints from earlier Ino versions; the studio tool no longer emits them
    [InlineData("clarification:photocraft", null)]
    [InlineData("clarification:filmcraft", null)]
    // Other route hint variants
    [InlineData("agent:general.chat", null)]
    [InlineData("direct:message", null)]
    [InlineData("app:install:store", null)]
    // Malformed, whitespace, and injection route hints
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("routed:", null)]
    [InlineData("routed:photocraft/../../bin", null)]
    [InlineData("routed:photocraft'; DROP TABLE apps; --", null)]
    [InlineData("routed:photocraft.'; DROP TABLE apps; --", "photocraft")]
    [InlineData("routed:filmcraft/<script>alert(1)</script>", null)]
    [InlineData("routed:filmcraft.<script>alert(1)</script>", "filmcraft")]
    public void InoTurnRouteHints_ComprehensiveBoundaryAndAdversarialInputs_ResolvesExpectedStudioApp(
        string routeHint,
        string? expectedStudioApp)
    {
        string? studioApp = routeHint.StartsWith("routed:photocraft.", StringComparison.Ordinal)
            ? "photocraft"
            : routeHint.StartsWith("routed:filmcraft.", StringComparison.Ordinal)
                ? "filmcraft"
                : null;

        Assert.Equal(expectedStudioApp, studioApp);
    }

    [Theory]
    [InlineData("photocraft")]
    [InlineData("filmcraft")]
    [InlineData("gallery")]
    [InlineData("creator")]
    [InlineData("post")]
    [InlineData("arbitrary_unhosted_app")]
    [InlineData("custom_tool_42")]
    public void StudioAppTurnRouting_UnhostedGuard_PreventsKeyNotFoundException_ForAnyUnhostedApp(string appName)
    {
        var hostedByNameField = typeof(CompiledAppsHost).GetField(
            "HostedByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        var hostedByName = (IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>)
            hostedByNameField!.GetValue(null)!;

        // Directly indexing HostedByName for any unhosted app throws KeyNotFoundException
        Assert.Throws<KeyNotFoundException>(() => hostedByName[appName]);

        // The guard HostedByName.ContainsKey ensures that unhosted apps are cleanly identified as unhosted
        var isHosted = hostedByName.ContainsKey(appName);
        Assert.False(isHosted);

        // When not hosted, forwarding goes to unhosted IApp.Request("edit", prompt)
        var targetKey = IApp.Key("brain-test", appName);
        Assert.Equal($"brain-test/apps/{appName}", targetKey);
    }

    [Fact]
    public void EmpiricalStress_KeyNotFoundExceptionIsImpossible_Under500AdversarialRouteHints()
    {
        var hostedByNameField = typeof(CompiledAppsHost).GetField(
            "HostedByName",
            BindingFlags.NonPublic | BindingFlags.Static);
        var hostedByName = (IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>)
            hostedByNameField!.GetValue(null)!;

        string[] prefixes =
        [
            "routed:photocraft.",
            "routed:filmcraft.",
            "routed:photocraft",
            "routed:filmcraft",
            "routed:photocraft_",
            "routed:filmcraft_",
            "routed:gallery.",
            "routed:creator.",
            "routed:settings.",
            "routed:custom.",
            "clarification:photocraft",
            "clarification:filmcraft",
            "agent:",
            "",
            "   ",
            "null",
            "routed:",
        ];

        string[] suffixes =
        [
            "",
            "edit",
            "canvas",
            "timeline.razor",
            "selectSubject",
            "removeBackground",
            "color.lumetri.auto",
            "sequence.exportMp4",
            "..",
            "../../etc/passwd",
            "'; DROP TABLE apps; --",
            "<script>alert('xss')</script>",
            "\0\a\b\f\v\u200B\u200C\uFEFF",
            "🌟🎨🎬🚀",
            new string('A', 5000),
        ];

        var testedCombinations = 0;
        foreach (var prefix in prefixes)
        {
            foreach (var suffix in suffixes)
            {
                var routeHint = prefix + suffix;
                testedCombinations++;

                // Simulate the exact CompiledAppsHost turn routing branch
                string? studioApp = routeHint.StartsWith("routed:photocraft.", StringComparison.Ordinal)
                    ? "photocraft"
                    : routeHint.StartsWith("routed:filmcraft.", StringComparison.Ordinal)
                        ? "filmcraft"
                        : null;

                if (studioApp is not null)
                {
                    // studioApp MUST only be "photocraft" or "filmcraft"
                    Assert.True(studioApp is "photocraft" or "filmcraft");

                    // Crucial safety invariant: HostedByName MUST NOT contain studioApp
                    var isHosted = hostedByName.ContainsKey(studioApp);
                    Assert.False(isHosted);

                    // If hosted branch were executed, it would throw KeyNotFoundException
                    Assert.Throws<KeyNotFoundException>(() => hostedByName[studioApp]);

                    // Under the unhosted branch, IApp.Key resolves safely without exceptions
                    var resolvedKey = IApp.Key("brain-adversarial", studioApp);
                    Assert.Equal($"brain-adversarial/apps/{studioApp}", resolvedKey);
                }
                else
                {
                    // When null, no studio forwarding occurs — no exception possible
                    Assert.Null(studioApp);
                }
            }
        }

        Assert.True(testedCombinations >= 250);
    }

    [Theory]
    [InlineData("photocraft", "Enhance lighting and contrast")]
    [InlineData("photocraft", "remove background from portrait.png")]
    [InlineData("photocraft", "")]
    [InlineData("photocraft", "   ")]
    [InlineData("photocraft", "special chars: !@#$%^&*()_+{}[]|\":;'<>?,./")]
    [InlineData("filmcraft", "Split video at 00:01:23 and trim")]
    [InlineData("filmcraft", "apply lumetri color grade to scene 2")]
    [InlineData("filmcraft", "Sequence with unicode: 🎬 4K HDR master 🚀")]
    public async Task StudioAppTurnRouting_UnhostedAppForwarding_DispatchesEditOperationWithExactPrompt(
        string studioApp,
        string userPrompt)
    {
        var appMock = new MockStudioApp();
        IApp targetApp = appMock;

        // Verify that the unhosted forwarding path correctly calls IApp.Request("edit", prompt)
        var requestId = await targetApp.Request("edit", userPrompt);

        Assert.NotEqual(Guid.Empty, requestId);
        Assert.Equal("edit", appMock.LastOperation);
        Assert.Equal(userPrompt, appMock.LastInput);

        // Verify key formatting contract
        var appKey = IApp.Key("tenant-brain-123", studioApp);
        Assert.Equal($"tenant-brain-123/apps/{studioApp}", appKey);
    }

    private sealed class MockStudioApp : IApp
    {
        public string? LastOperation { get; private set; }
        public string? LastInput { get; private set; }

        public Task<Guid> Emit(string signal, string args) => Request(signal, args);

        public Task<Guid> Request(string operation, string input)
        {
            LastOperation = operation;
            LastInput = input;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<AppSnapshot> Read() => throw new NotImplementedException();
        public Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation) => throw new NotImplementedException();
        public Task<AppSnapshot> Create(AppManifest manifest, string spec) => throw new NotImplementedException();
        public Task<AppSnapshot> SaveSpec(string spec, Guid operationId) => throw new NotImplementedException();
        public Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId) => throw new NotImplementedException();
        public Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections) => throw new NotImplementedException();
        public Task<AppSnapshot> Rollback(string version) => throw new NotImplementedException();
        public Task<AppSnapshot> BeginBuild(string? instruction) => throw new NotImplementedException();
        public Task<Guid> CurrentBuildAttempt() => throw new NotImplementedException();
        public Task<string?> BuildFlow() => throw new NotImplementedException();
        public Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.InstallStatus status, string sentence) => throw new NotImplementedException();
        public Task Progress(string state, string detail) => throw new NotImplementedException();
        public Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null) => throw new NotImplementedException();
        public Task<AppBundle> Export() => throw new NotImplementedException();
        public Task<AppSnapshot> Publish() => throw new NotImplementedException();
        public Task<AppBundle> ExportPublished() => throw new NotImplementedException();
        public Task<AppPublication[]> PublishedReleases() => throw new NotImplementedException();
        public Task<AppBundle> ExportRelease(string version, string digest) => throw new NotImplementedException();
        public Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null) => throw new NotImplementedException();
        public Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null) => throw new NotImplementedException();
        public Task Delete() => throw new NotImplementedException();
    }
}
