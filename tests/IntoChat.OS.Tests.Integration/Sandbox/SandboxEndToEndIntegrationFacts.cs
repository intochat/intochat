using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Composition.Machine;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Windows;
using IntoChat.OS.Sandbox;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Sandbox;

public sealed class SandboxEndToEndIntegrationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadsInvoicesPackageProcessesHostFileArchivesPersistsRowAndNotifies()
    {
        var tempDownloads = Directory.CreateTempSubdirectory("intochat-downloads-");
        try
        {
            await using var harness = await DualServerHarness.StartAsync(Ct);
            using var grantResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/windows/folders/grants",
                new
                {
                    appKey = "downloads-invoices",
                    slot = "downloads",
                    hostPath = tempDownloads.FullName,
                    access = FolderAccess.ReadWrite,
                    enabled = true,
                },
                Ct);
            Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

            var bundle = ShippedPackages.Find("downloads-invoices")!.ToAppBundle();
            using var importResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/apps/import",
                bundle,
                Ct);
            Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

            var invoicePath = Path.Combine(tempDownloads.FullName, "acme-invoice.txt");
            await File.WriteAllTextAsync(invoicePath, "Invoice #1042\nVendor: Acme Corp\nTotal: $249.00\n", Ct);

            var processedPath = Path.Combine(tempDownloads.FullName, "Processed", "acme-invoice.txt");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline && (!File.Exists(processedPath) || File.Exists(invoicePath)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);
            }

            Assert.True(File.Exists(processedPath), $"Expected archived invoice at '{processedPath}'.");
            Assert.False(File.Exists(invoicePath), "Expected original invoice file to be moved into Processed/.");

            var tables = harness.KernelServices.GetRequiredService<SdkTablesAdapter>();
            var rows = await tables.ListAsync(harness.Scope, "downloads-invoices", "invoices", 0, 50, Ct);
            var row = Assert.Single(rows, r => string.Equals(r.Key, "acme-invoice.txt", StringComparison.Ordinal));
            Assert.Contains("249.00", row.Json, StringComparison.Ordinal);

            NotificationInboxPayload? notifications = null;
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                notifications = await harness.KernelClient.GetFromJsonAsync<NotificationInboxPayload>(
                    $"/brains/{harness.Scope}/notifications",
                    Ct);
                if (notifications?.Items.Any(item => item.Title.Contains("Invoice processed: acme-invoice.txt", StringComparison.Ordinal)) == true)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
            }

            Assert.NotNull(notifications);
            var alert = Assert.Single(
                notifications!.Items,
                item => item.Title.Contains("Invoice processed: acme-invoice.txt", StringComparison.Ordinal));
            Assert.Equal("downloads-invoices", alert.SourceApp);
            Assert.Contains("249.00", alert.Body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempDownloads.FullName);
        }
    }

    [Fact]
    public async Task SandboxEdgeEnforcesAppTokenAndConfinesFolderTraversal()
    {
        var tempDownloads = Directory.CreateTempSubdirectory("intochat-confinement-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDownloads.FullName, "allowed.txt"), "safe-content", Ct);

            await using var harness = await DualServerHarness.StartAsync(Ct);
            using var grantResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/windows/folders/grants",
                new
                {
                    appKey = "downloads-invoices",
                    slot = "downloads",
                    hostPath = tempDownloads.FullName,
                    access = FolderAccess.ReadWrite,
                    enabled = true,
                },
                Ct);
            Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

            var invokePayload = new
            {
                contract = typeof(IFolder).FullName,
                method = nameof(IFolder.ReadTextAsync),
                slot = "downloads",
                argsJson = JsonSerializer.Serialize(new[] { "../secret.txt" }),
            };

            // 1. Missing token -> 401 Unauthorized
            using var missingTokenResponse = await harness.KernelClient.PostAsJsonAsync("/edge/sandbox/invoke", invokePayload, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, missingTokenResponse.StatusCode);

            // 2. Forged token -> 401 Unauthorized
            using var forgedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(invokePayload),
            };
            forgedRequest.Headers.TryAddWithoutValidation("X-App-Token", "forged.token.value");
            forgedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            forgedRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var forgedResponse = await harness.KernelClient.SendAsync(forgedRequest, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);

            // 2b. Valid token reading legitimate file inside granted slot -> 200 OK
            var tokens = harness.KernelServices.GetRequiredService<AppTokenService>();
            var validToken = tokens.Issue(harness.Scope, "downloads-invoices");
            using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(new
                {
                    contract = typeof(IFolder).FullName,
                    method = nameof(IFolder.ReadTextAsync),
                    slot = "downloads",
                    argsJson = JsonSerializer.Serialize(new[] { "allowed.txt" }),
                }),
            };
            allowedRequest.Headers.TryAddWithoutValidation("X-App-Token", validToken);
            allowedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            allowedRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var allowedResponse = await harness.KernelClient.SendAsync(allowedRequest, Ct);
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
            var allowedBody = await allowedResponse.Content.ReadAsStringAsync(Ct);
            Assert.Contains("safe-content", allowedBody, StringComparison.Ordinal);

            // 3. Valid token attempting '../secret.txt' traversal -> 403 Forbidden
            using var traversalRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(invokePayload),
            };
            traversalRequest.Headers.TryAddWithoutValidation("X-App-Token", validToken);
            traversalRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            traversalRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var traversalResponse = await harness.KernelClient.SendAsync(traversalRequest, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, traversalResponse.StatusCode);

            // 4. Valid token for another ungranted app -> 403 Forbidden
            var ungrantedToken = tokens.Issue(harness.Scope, "other-app");
            using var ungrantedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(new
                {
                    contract = typeof(IFolder).FullName,
                    method = nameof(IFolder.ListAsync),
                    slot = "downloads",
                    argsJson = "[]",
                }),
            };
            ungrantedRequest.Headers.TryAddWithoutValidation("X-App-Token", ungrantedToken);
            ungrantedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            ungrantedRequest.Headers.TryAddWithoutValidation("X-App-Key", "other-app");
            using var ungrantedResponse = await harness.KernelClient.SendAsync(ungrantedRequest, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, ungrantedResponse.StatusCode);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempDownloads.FullName);
        }
    }

    [Fact]
    public async Task OutOfProcUnitHostCompilesAndExecutesRuntimeSynapseThroughAccessGate()
    {
        await using var harness = await DualServerHarness.StartAsync(Ct, withExternalSandboxUrl: false);
        var bridge = harness.KernelServices.GetRequiredService<ISandboxRuntimeBridge>();
        var appCompiler = new AppPackageCompiler();
        var synapseCompiler = new SynapseCompiler(ReferenceSet.FromDefaultContext());

        // A) Pure [State]/[Synapse] runtime unit compiled via SynapseCompiler orchestrating INotifications slot through AccessGate
        const string synapseCode = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain;
            using DigitalBrain.UI;
            using INotifySlot = IntoChat.OS.Modules.Apps.Notifications.INotifications;

            namespace RuntimeGuardSynapse;

            [Signal("synapse.alert.check")]
            public sealed record SynapseAlertCheck(string Tag) : Signal;

            [State("synapse.alert")]
            public sealed record SynapseAlertState(int Checks = 0, string LastReceiptId = "")
            {
                public SynapseAlertState() : this(0, "") { }
            }

            public static class AlertSynapse
            {
                [Synapse(typeof(SynapseAlertCheck), "synapse.alert")]
                public static async Task<Reaction<SynapseAlertState>> OnCheck(
                    SynapseAlertCheck signal,
                    SynapseAlertState state,
                    [DigitalBrain.Slot(Name = "inbox")] INotifySlot notifications,
                    CancellationToken ct)
                {
                    var receipt = await notifications.Notify(
                        $"Synapse Alert {signal.Tag}",
                        "Dispatched via NeuronProxy",
                        "normal",
                        ct);
                    var receiptId = receipt.GetProperty("id").GetString() ?? "";
                    return Reaction<SynapseAlertState>.Of(state with
                    {
                        Checks = state.Checks + 1,
                        LastReceiptId = receiptId,
                    });
                }

                [Synapse("synapse.alert")]
                public static View Render(SynapseAlertState state)
                    => new("Synapse Alert", new Text($"checks={state.Checks};receipt={state.LastReceiptId}"));
            }
            """;

        var synapseBuild = synapseCompiler.Compile(new SynapseSource(
            "synapse.alert",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AlertSynapse.cs"] = synapseCode },
            null));
        Assert.True(synapseBuild.Success, string.Join("\n", synapseBuild.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}")));

        var synapsePkg = new AppPackage(
            Key: "synapse.alert",
            Version: "1.0.0",
            Title: "Synapse Alert",
            Summary: "Runtime synapse exercising INotifications slot through AccessGate",
            Icon: "bell",
            Category: "Synapses",
            Author: "test",
            SourceFiles: [new AppSourceFile("AlertSynapse.cs", synapseCode)],
            Needs: new AppPackageNeeds([], ["synapse.alert.check"], [], []),
            DefaultSettingsJson: "{}",
            Digest: synapseBuild.Hash);

        // B) Runtime C# BrainApp compiled via AppPackageCompiler (SynapseCompiler) awaiting IMemory directly via Brain.Slot<IMemory>
        const string memoryAppCode = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain.UI;
            using IntoChat.OS.Modules.AI.Memory;
            using IntoChat.Sdk;

            namespace RuntimeMemoryGuard;

            public sealed class MemoryGuardState
            {
                public int Pid { get; set; }
                public int Checks { get; set; }
                public string LastSubject { get; set; } = string.Empty;
            }

            public sealed class MemoryGuardSettings
            {
                public string Slot { get; set; } = "default";
            }

            [App("memory.guard", Title = "Memory Guard", Summary = "Orchestrates IMemory via NeuronProxy")]
            public sealed class MemoryGuardApp : BrainApp<MemoryGuardState, MemoryGuardSettings>
            {
                [OnSignal("memory.guard.check")]
                public async Task OnCheck(string? tag, CancellationToken ct)
                {
                    var memory = Brain.Slot<IMemory>(Settings.Slot);
                    var episode = await memory.CaptureEpisode($"rule-{tag ?? "run"}", "Use sandboxed synapses");
                    var snapshot = await memory.Read();
                    State.Pid = Environment.ProcessId;
                    State.Checks++;
                    State.LastSubject = $"{episode.Agent}:episodes={snapshot.Episodes.Length}";
                    await Brain.Notifications.NotifyAsync(
                        $"Memory Guard {tag ?? "run"}",
                        $"recorded={State.LastSubject}",
                        NotificationPriority.Normal,
                        ct);
                }

                public override View Render()
                    => new("Memory Guard", new Text($"pid={State.Pid};checks={State.Checks};last={State.LastSubject}"));
            }
            """;

        var memoryCompiled = appCompiler.Compile(new AppPackage(
            Key: "memory.guard",
            Version: "1.0.0",
            Title: "Memory Guard",
            Summary: "Orchestrates IMemory via NeuronProxy",
            Icon: "brain",
            Category: "Synapses",
            Author: "test",
            SourceFiles: [new AppSourceFile("MemoryGuard.cs", memoryAppCode)],
            Needs: AppPackageNeeds.Empty,
            DefaultSettingsJson: "{}",
            Digest: string.Empty));
        Assert.True(memoryCompiled.Succeeded, string.Join("\n", memoryCompiled.Errors));

        var deploySynapse = await bridge.DeployPackageAsync(harness.Scope, synapsePkg, synapseBuild.Assembly, null, null, Ct);
        Assert.True(deploySynapse.Succeeded, deploySynapse.Error);

        var deployMemory = await bridge.DeployPackageAsync(harness.Scope, memoryCompiled.EnrichedPackage!, memoryCompiled.AssemblyBytes, null, null, Ct);
        Assert.True(deployMemory.Succeeded, deployMemory.Error);

        try
        {
            // 1. Fail-closed before any grants exist for either unit/app
            var ungrantedSynapse = await bridge.DispatchSignalAsync(harness.Scope, "synapse.alert", "synapse.alert.check", "{\"tag\":\"s1\"}", Ct);
            Assert.NotNull(ungrantedSynapse.Error);
            Assert.Contains("grant", ungrantedSynapse.Error, StringComparison.OrdinalIgnoreCase);

            var ungrantedMemory = await bridge.DispatchSignalAsync(harness.Scope, "memory.guard", "memory.guard.check", "\"m1\"", Ct);
            Assert.NotNull(ungrantedMemory.Error);
            Assert.Contains("grant", ungrantedMemory.Error, StringComparison.OrdinalIgnoreCase);

            // 2. Grant AccessGate permission for both slots via real Kernel grants API, dispatch out-of-proc, verify success & distinct child PID
            var notifGrant = Grant.Tuple(
                $"brain:{harness.Scope}",
                "app:synapse.alert",
                $"notifications.connection/{harness.Scope}/inbox",
                GrantRelations.Run) with
            { Id = "grant-synapse-alert-notif" };
            var memoryGrant = Grant.Tuple(
                $"brain:{harness.Scope}",
                "app:memory.guard",
                $"ai.memory/{harness.Scope}/default",
                GrantRelations.Run) with
            { Id = "grant-memory-guard-default" };

            using (var g1 = await harness.KernelClient.PostAsJsonAsync($"/brains/{harness.Scope}/grants", notifGrant, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, g1.StatusCode);
            }

            using (var g2 = await harness.KernelClient.PostAsJsonAsync($"/brains/{harness.Scope}/grants", memoryGrant, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, g2.StatusCode);
            }

            var grantedSynapse = await bridge.DispatchSignalAsync(harness.Scope, "synapse.alert", "synapse.alert.check", "{\"tag\":\"s2\"}", Ct);
            Assert.True(grantedSynapse.Handled, grantedSynapse.Error);
            Assert.Null(grantedSynapse.Error);

            var grantedMemory = await bridge.DispatchSignalAsync(harness.Scope, "memory.guard", "memory.guard.check", "\"m2\"", Ct);
            Assert.True(grantedMemory.Handled, grantedMemory.Error);
            Assert.Null(grantedMemory.Error);

            var memoryView = await bridge.RenderAsync(harness.Scope, "memory.guard", Ct);
            Assert.NotNull(memoryView);
            var memoryViewJson = JsonSerializer.Serialize(memoryView, JsonSerializerOptions.Web);
            Assert.Contains("checks=1;last=rule-m2:episodes=1", memoryViewJson, StringComparison.Ordinal);
            Assert.DoesNotContain($"pid={Environment.ProcessId};", memoryViewJson, StringComparison.Ordinal);

            var afterNotifs = await harness.KernelClient.GetFromJsonAsync<NotificationInboxPayload>(
                $"/brains/{harness.Scope}/notifications",
                Ct);
            Assert.Contains(afterNotifs?.Items ?? [], item => item.Title == "Synapse Alert s2");
            var memoryAlert = Assert.Single(afterNotifs?.Items ?? [], item => item.Title == "Memory Guard m2");
            Assert.Equal("memory.guard", memoryAlert.SourceApp);

            // 3. Revoke grants and verify AccessGate fails closed again
            using (var r1 = await harness.KernelClient.PostAsJsonAsync($"/brains/{harness.Scope}/grants/revoke", new { id = notifGrant.Id }, Ct))
            {
                r1.EnsureSuccessStatusCode();
            }

            using (var r2 = await harness.KernelClient.PostAsJsonAsync($"/brains/{harness.Scope}/grants/revoke", new { id = memoryGrant.Id }, Ct))
            {
                r2.EnsureSuccessStatusCode();
            }

            var revokedSynapse = await bridge.DispatchSignalAsync(harness.Scope, "synapse.alert", "synapse.alert.check", "{\"tag\":\"s3\"}", Ct);
            Assert.NotNull(revokedSynapse.Error);
            Assert.Contains("grant", revokedSynapse.Error, StringComparison.OrdinalIgnoreCase);
            var revokedMemory = await bridge.DispatchSignalAsync(harness.Scope, "memory.guard", "memory.guard.check", "\"m3\"", Ct);
            Assert.NotNull(revokedMemory.Error);
            Assert.Contains("grant", revokedMemory.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await bridge.UnloadAsync(harness.Scope, "synapse.alert", Ct);
            await bridge.UnloadAsync(harness.Scope, "memory.guard", Ct);
        }
    }

    [Fact]
    public async Task OutOfProcUnitHostPublishesLiveViewToViewNeuronAndRefusesUnauthorizedApps()
    {
        await using var harness = await DualServerHarness.StartAsync(Ct, withExternalSandboxUrl: false);
        var bridge = harness.KernelServices.GetRequiredService<ISandboxRuntimeBridge>();
        var appCompiler = new AppPackageCompiler();

        const string viewAppCode = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain.UI;
            using IntoChat.Sdk;

            namespace RuntimeViewGuard;

            public sealed class ViewGuardState
            {
                public int Count { get; set; }
                public string LastLabel { get; set; } = "initial";
            }

            public sealed class ViewGuardSettings { }

            [App("view.guard", Title = "View Guard", Summary = "Publishes live ViewNeuron state via --unit-host")]
            public sealed class ViewGuardApp : BrainApp<ViewGuardState, ViewGuardSettings>
            {
                [OnSignal("view.guard.ping")]
                public Task OnPing(string? label, CancellationToken ct)
                {
                    State.Count++;
                    State.LastLabel = string.IsNullOrWhiteSpace(label) ? "ping" : label;
                    return Task.CompletedTask;
                }

                public override View Render()
                    => new("View Guard", new Column([
                        new Text($"count={State.Count};label={State.LastLabel}"),
                        new Field("note", "apps/view.guard/note", FieldKind.Text),
                    ]));
            }
            """;

        var compiled = appCompiler.Compile(new AppPackage(
            Key: "view.guard",
            Version: "1.0.0",
            Title: "View Guard",
            Summary: "Publishes live ViewNeuron state via --unit-host",
            Icon: "eye",
            Category: "Synapses",
            Author: "test",
            SourceFiles: [new AppSourceFile("ViewGuard.cs", viewAppCode)],
            Needs: AppPackageNeeds.Empty,
            DefaultSettingsJson: "{}",
            Digest: string.Empty));
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));

        var deployment = await bridge.DeployPackageAsync(harness.Scope, compiled.EnrichedPackage!, compiled.AssemblyBytes, null, null, Ct);
        Assert.True(deployment.Succeeded, deployment.Error);

        try
        {
            using (var initialViewResponse = await harness.KernelClient.GetAsync($"/brains/{harness.Scope}/apps/view.guard/view", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, initialViewResponse.StatusCode);
                var initialBody = await initialViewResponse.Content.ReadAsStringAsync(Ct);
                Assert.Contains("count=0;label=initial", initialBody, StringComparison.Ordinal);
            }

            var dispatched = await bridge.DispatchSignalAsync(harness.Scope, "view.guard", "view.guard.ping", "\"alpha\"", Ct);
            Assert.True(dispatched.Handled, dispatched.Error);
            Assert.Null(dispatched.Error);

            using (var updatedViewResponse = await harness.KernelClient.GetAsync($"/brains/{harness.Scope}/apps/view.guard/view", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, updatedViewResponse.StatusCode);
                var updatedBody = await updatedViewResponse.Content.ReadAsStringAsync(Ct);
                Assert.Contains("count=1;label=alpha", updatedBody, StringComparison.Ordinal);
            }

            // Reading via /apps/node?kind=view returns the sandbox view without hitting Orleans screen/unit BindingException noise.
            var viewKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(harness.Scope), "view.guard").ViewKey;
            using (var nodeViewResponse = await harness.KernelClient.GetAsync($"/brains/{harness.Scope}/apps/node?kind=view&name={Uri.EscapeDataString(viewKey)}", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, nodeViewResponse.StatusCode);
                var nodeViewBody = await nodeViewResponse.Content.ReadAsStringAsync(Ct);
                Assert.Contains("count=1;label=alpha", nodeViewBody, StringComparison.Ordinal);
            }

            // Reading via /apps/node?kind=viewport resolves the sandbox view when a Desktop window targets view.guard.
            var viewGuardBrainId = new BrainId(harness.Scope);
            var viewGuardAppKey = IntoChat.OS.Host.Primitives.AppRef.Of(viewGuardBrainId, "view.guard").Key;
            var desktopVpId = IntoChat.OS.Host.Primitives.AppRef.Of(viewGuardBrainId, "desktop").ChildNeuron("viewport", "window/w-sandbox");
            var edgeAuth = harness.KernelServices.GetRequiredService<EdgeAuthority>();
            using (edgeAuth.Enter(edgeAuth.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, viewGuardBrainId)))
            {
                var vp = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Viewport.IViewport>(harness.KernelServices, desktopVpId);
                var vpState = await vp.Read();
                await vp.Set(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef("view.guard", viewGuardAppKey, "full", ""), vpState.Revision);
            }
            var desktopVpKey = IntoChat.OS.Host.Primitives.AppRef.Of(viewGuardBrainId, "desktop").ChildKey("window/w-sandbox");
            using (var nodeVpResponse = await harness.KernelClient.GetAsync($"/brains/{harness.Scope}/apps/node?kind=viewport&name={Uri.EscapeDataString(desktopVpKey)}", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, nodeVpResponse.StatusCode);
                var nodeVpBody = await nodeVpResponse.Content.ReadAsStringAsync(Ct);
                Assert.Contains(viewKey, nodeVpBody, StringComparison.Ordinal);
            }

            // Unauthorized app cannot publish over view.guard's ViewNeuron even inside the same brain.
            var brainId = new BrainId(harness.Scope);
            var edge = harness.KernelServices.GetRequiredService<EdgeAuthority>();
            var otherAppKey = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "other-app").Key;
            var viewId = new NeuronId(brainId, UIVocabulary.ViewType, IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "view.guard").ViewPath);
            using (edge.Enter(edge.App(otherAppKey, new Actor.Person(AppPrincipal.Id(otherAppKey), AccountSession.DefaultLogin), brainId)))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    SiloNeurons.Get<IView>(harness.KernelServices, viewId)
                        .Publish(99, new View("Hijacked", new Text("spoof"))));
            }
        }
        finally
        {
            await bridge.UnloadAsync(harness.Scope, "view.guard", Ct);
        }
    }

    [Fact]
    public async Task AppsImportEndpoint_HandlesOmittedCollectionsAndMissingFieldsWithoutInternalError()
    {
        await using var harness = await DualServerHarness.StartAsync(Ct, withExternalSandboxUrl: false);

        const string minimalBundleJson = """
            {
              "name": "minimal-import-app",
              "version": "1.0.0",
              "manifest": {
                "title": "Minimal Import",
                "description": "Omitted operations, settings, and slots"
              },
              "spec": "Minimal spec"
            }
            """;

        using var minimalRequest = new HttpRequestMessage(HttpMethod.Post, $"/brains/{harness.Scope}/apps/import")
        {
            Content = new StringContent(minimalBundleJson, Encoding.UTF8, "application/json"),
        };
        using var minimalResponse = await harness.KernelClient.SendAsync(minimalRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, minimalResponse.StatusCode);

        const string missingManifestJson = """
            {
              "name": "broken-missing-manifest",
              "version": "1.0.0"
            }
            """;
        using var missingManifestRequest = new HttpRequestMessage(HttpMethod.Post, $"/brains/{harness.Scope}/apps/import")
        {
            Content = new StringContent(missingManifestJson, Encoding.UTF8, "application/json"),
        };
        using var missingManifestResponse = await harness.KernelClient.SendAsync(missingManifestRequest, Ct);
        Assert.True((int)missingManifestResponse.StatusCode is >= 400 and < 500, $"Expected 4xx for missing manifest, got {missingManifestResponse.StatusCode}");

        const string missingVersionJson = """
            {
              "name": "broken-missing-version",
              "manifest": {
                "title": "No Version",
                "description": "Missing version"
              }
            }
            """;
        using var missingVersionRequest = new HttpRequestMessage(HttpMethod.Post, $"/brains/{harness.Scope}/apps/import")
        {
            Content = new StringContent(missingVersionJson, Encoding.UTF8, "application/json"),
        };
        using var missingVersionResponse = await harness.KernelClient.SendAsync(missingVersionRequest, Ct);
        Assert.True((int)missingVersionResponse.StatusCode is >= 400 and < 500, $"Expected 4xx for missing version, got {missingVersionResponse.StatusCode}");
    }

    private sealed record NotificationInboxPayload(NotificationItem[] Items, int UnreadCount);

    private sealed class DualServerHarness : IAsyncDisposable
    {
        private readonly WebApplication? _sandboxApp;
        private readonly WebApplication _kernelApp;
        private readonly PortLease _ports;
        private readonly string _homeDir;

        private DualServerHarness(
            WebApplication? sandboxApp,
            WebApplication kernelApp,
            HttpClient kernelClient,
            PortLease ports,
            string homeDir,
            string scope)
        {
            _sandboxApp = sandboxApp;
            _kernelApp = kernelApp;
            KernelClient = kernelClient;
            _ports = ports;
            _homeDir = homeDir;
            Scope = scope;
        }

        public HttpClient KernelClient { get; }
        public IServiceProvider KernelServices => _kernelApp.Services;
        public string Scope { get; }

        public static async Task<DualServerHarness> StartAsync(CancellationToken ct, bool withExternalSandboxUrl = true)
        {
            var enrollKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var signingMaterial = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var homeDir = Path.Combine(Path.GetTempPath(), "intochat-os-sandbox-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(homeDir);

            var ports = PortReservation.ReservePair();
            WebApplication? sandboxApp = null;
            WebApplication? kernelApp = null;
            HttpClient? kernelClient = null;
            try
            {
                sandboxApp = withExternalSandboxUrl ? await StartSandboxWorkerAsync(enrollKey, ct) : null;
                var sandboxUrl = sandboxApp?.Urls.First() ?? string.Empty;

                var kernelBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    EnvironmentName = "Development",
                    ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
                    ContentRootPath = Path.GetTempPath(),
                });
                kernelBuilder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                kernelBuilder.WebHost.UseUrls("http://127.0.0.1:0");

                var sandboxOptionsPrefix = ModuleConfiguration.ModuleOptionsSection(SandboxOptions.SectionName);
                var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                    [IntoChatOSNames.HomeKey] = homeDir,
                    [MachineHosting.IdFileKey] = Path.Combine(homeDir, "machine-id"),
                    [IntoChatOSNames.ShellEnabledKey] = "false",
                    [IntoChatOSNames.FrontEnabledKey] = "false",
                    [AuthKeys.Posture] = "Open",
                    [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
                    [$"{sandboxOptionsPrefix}:EnrollKey"] = enrollKey,
                    [$"{sandboxOptionsPrefix}:TokenSigningMaterial"] = signingMaterial,
                };
                if (!string.IsNullOrEmpty(sandboxUrl))
                {
                    config[$"{sandboxOptionsPrefix}:Url"] = sandboxUrl;
                }
                kernelBuilder.Configuration.AddInMemoryCollection(config);

                LocalRunDefaults.Apply(kernelBuilder.Configuration);
                OsModule[] modules =
                [
                    .. OperatingSystemModules.InProcessModules,
                    OsModule.FromDigital(typeof(WindowsModule)),
                ];
                kernelBuilder.AddIntoChatOSServer(server => server.AddOperatingSystem(modules));

                kernelApp = kernelBuilder.Build();
                kernelApp.MapIntoChatOSServer();
                await kernelApp.StartAsync(ct);

                var kernelUrl = kernelApp.Urls.First();
                kernelApp.Services.GetRequiredService<IOptions<SandboxOptions>>().Value.KernelCallbackUrl = kernelUrl;

                kernelClient = new HttpClient { BaseAddress = new Uri(kernelUrl, UriKind.Absolute) };
                var presence = kernelApp.Services.GetRequiredService<IntoChat.OS.Modules.Identity.Accounts.IPresenceProof>();
                kernelClient.DefaultRequestHeaders.Add(IntoChat.OS.Modules.Identity.Contracts.Accounts.PresenceProof.HeaderName, presence.Token);
                var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

                var ready = false;
                var healthDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < healthDeadline)
                {
                    try
                    {
                        using var readyRes = await kernelClient.GetAsync("/ready", ct);
                        using var foldersRes = await kernelClient.GetAsync($"/brains/{scope}/windows/folders", ct);
                        if (readyRes.IsSuccessStatusCode && foldersRes.IsSuccessStatusCode)
                        {
                            ready = true;
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                }

                if (!ready)
                {
                    throw new TimeoutException($"Kernel server at '{kernelUrl}' did not become ready within 30 seconds.");
                }

                return new DualServerHarness(sandboxApp, kernelApp, kernelClient, ports, homeDir, scope);
            }
            catch
            {
                kernelClient?.Dispose();
                if (kernelApp is not null)
                {
                    await kernelApp.DisposeAsync();
                }

                if (sandboxApp is not null)
                {
                    await sandboxApp.DisposeAsync();
                }

                ports.Dispose();
                DeleteDirectoryIfPresent(homeDir);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                KernelClient.Dispose();
                try
                {
                    await _kernelApp.StopAsync();
                }
                finally
                {
                    await _kernelApp.DisposeAsync();
                }

                if (_sandboxApp is not null)
                {
                    try
                    {
                        await _sandboxApp.StopAsync();
                    }
                    finally
                    {
                        await _sandboxApp.DisposeAsync();
                    }
                }
            }
            finally
            {
                _ports.Dispose();
                DeleteDirectoryIfPresent(_homeDir);
            }
        }

        private static async Task<WebApplication> StartSandboxWorkerAsync(string enrollKey, CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(SandboxEndToEndIntegrationFacts).Assembly.GetName().Name,
                ContentRootPath = Path.GetTempPath(),
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddHttpClient("SandboxKernel");
            builder.Services.AddSingleton(sp => new AppSandboxHost(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SandboxKernel"),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AppSandboxHost>>()));

            var app = builder.Build();
            app.MapGet("/health", (AppSandboxHost host) => Results.Ok(new { status = "ok", loadedApps = host.LoadedAppCount }));

            var enrollKeyBytes = Encoding.UTF8.GetBytes(enrollKey);
            var apps = app.MapGroup("/apps");
            apps.AddEndpointFilter(async (context, next) =>
            {
                if (!context.HttpContext.Request.Headers.TryGetValue("X-Sandbox-Enroll-Key", out var provided)
                    || provided.Count != 1)
                {
                    return Results.Unauthorized();
                }

                var providedBytes = Encoding.UTF8.GetBytes(provided[0] ?? string.Empty);
                if (providedBytes.Length != enrollKeyBytes.Length
                    || !CryptographicOperations.FixedTimeEquals(providedBytes, enrollKeyBytes))
                {
                    return Results.Unauthorized();
                }

                return await next(context).ConfigureAwait(false);
            });

            apps.MapPost("/deploy", async (DeployAppRequest req, AppSandboxHost host, CancellationToken reqCt)
                => Results.Ok(await host.DeployAsync(req, reqCt).ConfigureAwait(false)));

            apps.MapPost("/dispatch", async (DispatchAppRequest req, AppSandboxHost host, CancellationToken reqCt) =>
            {
                try
                {
                    return Results.Ok(await host.DispatchAsync(req, reqCt).ConfigureAwait(false));
                }
                catch (KeyNotFoundException ex)
                {
                    return Results.NotFound(new { error = ex.Message });
                }
            });

            apps.MapPost("/unload", async (UnloadAppRequest req, AppSandboxHost host, CancellationToken reqCt)
                => Results.Ok(await host.UnloadAsync(req.BrainId, req.AppKey, reqCt).ConfigureAwait(false)));

            await app.StartAsync(ct);
            return app;
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
        => DigitalBrain.Testing.TempDirectory.DeleteIfPresent(path);
}
