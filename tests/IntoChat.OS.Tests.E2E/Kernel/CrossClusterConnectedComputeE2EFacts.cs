using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Notifications;
using IntoChat.OS.Runtime;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// Real Cross-Cluster OS &lt;-&gt; Cloud Integration / E2E test suite (INT-2496 Slice 4b).
/// Stands up 3 real loopback Kestrel servers in-process (zero Aspire, zero Docker, zero fake shortcuts):
/// 1) Hosted Cloud OS Cluster (real IntoChat.OS Kestrel + Orleans silo),
/// 2) Real HTTP Circle Relay Server (locked Slice 1 wire contract + ES256 ServiceAssertion + P-256 X-Machine-Proof + /live),
/// 3) Local Windows/Desktop PC OS Cluster (real IntoChat.OS Kestrel + Orleans silo + DeviceWorker + real UnitHost + LocalPcResourceExecutor).
/// </summary>
public sealed class CrossClusterConnectedComputeE2EFacts(CrossClusterConnectedComputeFixture fixture)
    : IClassFixture<CrossClusterConnectedComputeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CallbackNotificationScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                var receipt = await os.Notifications.NotifyAsync(
                    "cross-cluster-callback-title",
                    "hello-from-confined-local-pc",
                    NotificationPriority.Normal,
                    ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: $"Notified:{receipt.Id}",
                    Artifacts: [new OrchestrationArtifact("receipt.txt", "text/plain", receipt.Id)],
                    Metrics: new Dictionary<string, double> { ["notified"] = 1 });
            }
        }
        """;

    private const string OutOfScopeNotificationScript = """
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                _ = await os.Notifications.ListAsync(5, false, ct);
                return new OrchestrationResult("succeeded", "should-never-succeed");
            }
        }
        """;

    private const string PureComputeScript = """
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                using var doc = JsonDocument.Parse(inputJson);
                var n = doc.RootElement.TryGetProperty("count", out var prop) ? prop.GetInt32() : 0;
                return Task.FromResult(new OrchestrationResult(
                    Status: "succeeded",
                    Summary: $"Computed:{n * 3}",
                    Artifacts: [],
                    Metrics: new Dictionary<string, double> { ["tripled"] = n * 3 }));
            }
        }
        """;

    [Fact]
    [Trait("Category", "CrossClusterE2E")]
    public async Task Test01_OptionA_AntiDriveByEnrollment_StagesCancelsConfirms_AndAdvertisesCapabilities()
    {
        await fixture.EnsureUnlinkedAsync(Ct);
        Assert.False(File.Exists(fixture.LocalPc.LinkBinPath));

        // 1. Browser 1-click flow: GET /account/link-device -> Hosted OS POST /devices/enrollment/mint (ServiceAssertion).
        var linkDevice = await fixture.RequestLinkDeviceUriAsync(Ct);
        Assert.True(DeviceEnrollUri.TryParse(linkDevice.Uri, out var parsedServer, out var parsedCode, out var parseError), parseError);
        Assert.Equal(fixture.Relay.Origin + "/", parsedServer!.AbsoluteUri);
        Assert.False(string.IsNullOrWhiteSpace(parsedCode.Value));

        // 2. Anti-drive-by staging: POST /brains/owner/devices/link/pending -> link.bin MUST NOT be created yet.
        using (var stageRes = await fixture.SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{fixture.LocalPc.Scope}/devices/link/pending",
            JsonSerializer.Serialize(new { uri = linkDevice.Uri }),
            Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, stageRes.StatusCode);
        }

        Assert.False(File.Exists(fixture.LocalPc.LinkBinPath));
        var statusWhilePending = await fixture.GetLocalPcLinkStatusAsync(Ct);
        Assert.Equal(LinkState.NotJoined, statusWhilePending.State);

        // 3. Cancel via /link/pending/cancel -> clears pending without joining.
        using (var cancelRes = await fixture.SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{fixture.LocalPc.Scope}/devices/link/pending/cancel",
            bodyJson: null,
            Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelRes.StatusCode);
        }

        using (var pendingAfterCancel = await fixture.SendLocalPcAsPersonAsync(
            HttpMethod.Get,
            $"/brains/{fixture.LocalPc.Scope}/devices/link/pending",
            bodyJson: null,
            Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, pendingAfterCancel.StatusCode);
        }

        Assert.False(File.Exists(fixture.LocalPc.LinkBinPath));

        // 4. Re-stage and explicitly confirm via /link/pending/confirm -> real Join -> POST /circle/v1/enroll -> link.bin sealed.
        using (var restageRes = await fixture.SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{fixture.LocalPc.Scope}/devices/link/pending",
            JsonSerializer.Serialize(new { uri = linkDevice.Uri }),
            Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, restageRes.StatusCode);
        }

        using (var confirmRes = await fixture.SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{fixture.LocalPc.Scope}/devices/link/pending/confirm",
            bodyJson: null,
            Ct))
        {
            Assert.Equal(HttpStatusCode.OK, confirmRes.StatusCode);
            var confirmed = JsonSerializer.Deserialize(
                await confirmRes.Content.ReadAsStringAsync(Ct),
                DeviceWireJson.Default.LinkStatus);
            Assert.NotNull(confirmed);
            Assert.Equal(LinkState.Connected, confirmed!.State);
        }

        Assert.True(File.Exists(fixture.LocalPc.LinkBinPath));

        // 5. DeviceWorker wakes and advertises ICodeSandbox via X-Machine-Proof-signed PUT /circle/v1/compute/capability;
        //    Hosted OS discovers the PC via ServiceAssertion-authenticated GET /circle/v1/compute/nodes.
        var discovered = await fixture.WaitForHostedNodeAsync(
            node => node.MachineId == fixture.LocalPc.MachineId
                && node.Status == ComputeNodeStatus.Online
                && node.IsEligibleForSovereignCompute,
            Ct);
        Assert.NotNull(discovered);
        Assert.True(discovered!.AdvertisesCodeSandbox);
        Assert.True(fixture.Relay.CapabilityReportsReceived > 0);
        Assert.True(fixture.Relay.ServiceAssertionCallsVerified > 0);

        // 6. Security gate: ServiceAssertion MUST receive 401 if sent to worker routes!
        var signer = fixture.HostedOs.Services.GetRequiredService<IServiceAssertionSigner>();
        var capPath = DeviceRoutes.ComputeCapability;
        var capBody = Encoding.UTF8.GetBytes("{}");
        var serviceJwt = signer.Sign(fixture.HostedOs.Scope, "circle-compute-dispatch", "PUT", capPath, capBody);
        using var badWorkerReq = new HttpRequestMessage(HttpMethod.Put, fixture.Relay.Origin + capPath)
        {
            Content = new ByteArrayContent(capBody),
        };
        badWorkerReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        badWorkerReq.Headers.Authorization = new AuthenticationHeaderValue(ServiceAssertion.Scheme, serviceJwt);
        using var badWorkerRes = await fixture.Http.SendAsync(badWorkerReq, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, badWorkerRes.StatusCode);
    }

    [Fact]
    [Trait("Category", "CrossClusterE2E")]
    public async Task Test02_Tier1_SandboxDispatch_RunsRealUnitHostOnLocalPc_AndCallsBackOverKernelCallbackUrl()
    {
        await fixture.EnsureLinkedAsync(allowFolders: false, allowConsole: false, Ct);
        fixture.HostedOs.ClearSandboxEdgeHits();

        var compiler = new AppPackageCompiler();
        var brain = new BrainId(fixture.HostedOs.Scope);
        var sandbox = fixture.HostedOs.Services.GetRequiredService<ISandboxRuntimeBridge>();

        // 1. Dispatch a compiled BrainApp that calls os.Notifications.NotifyAsync(...) over KernelCallbackUrl -> Hosted OS /edge/sandbox/invoke.
        var appKey = CodeOrchestrationScriptWrapper.AppKey("cross-cluster-cb-1");
        var compiled = compiler.Compile(CodeOrchestrationScriptWrapper.BuildPackage(appKey, "Cross-Cluster Callback", CallbackNotificationScript));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));

        var dispatch = await ((ICodeSandbox)sandbox).ExecuteScopedCommandAsync(
            brain,
            compiled.EnrichedPackage!,
            compiled.AssemblyBytes,
            CodeOrchestrationScriptWrapper.Command,
            JsonSerializer.Serialize("{}"),
            allowedContracts: ["notifications"],
            cancellationToken: Ct);

        Assert.True(dispatch.Handled, dispatch.Error);
        Assert.Null(dispatch.Error);
        Assert.True(
            string.Equals("connected-client", dispatch.ExecutionTarget, StringComparison.Ordinal),
            $"Expected connected-client, got '{dispatch.ExecutionTarget}' (relayDiag='{fixture.Relay.LastSandboxDispatchDiag}', sandboxPolls={fixture.Relay.SandboxPollCount}).");
        Assert.Equal(fixture.LocalPc.MachineId, dispatch.ExecutedByMachineId);
        Assert.NotNull(dispatch.ResultJson);

        using var orchDoc = ParseOrchestrationResult(dispatch.ResultJson!);
        var orchRoot = orchDoc.RootElement;
        Assert.Equal("succeeded", orchRoot.GetProperty("status").GetString());
        var summary = orchRoot.GetProperty("summary").GetString() ?? string.Empty;
        Assert.StartsWith("Notified:", summary, StringComparison.Ordinal);
        var notificationId = summary["Notified:".Length..];
        Assert.False(string.IsNullOrWhiteSpace(notificationId));

        // Prove the callback hit Hosted OS /edge/sandbox/invoke with X-App-Token and persisted in Hosted OS INotificationsInbox.
        Assert.Contains(fixture.HostedOs.SandboxEdgeHits, hit => hit.Path == "/edge/sandbox/invoke" && hit.HasAppToken);

        var hostedEdge = fixture.HostedOs.Services.GetRequiredService<EdgeAuthority>();
        using (hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain)))
        {
            var inbox = SiloNeurons.Get<INotificationsInbox>(fixture.HostedOs.Services, INotificationsInbox.Key(fixture.HostedOs.Scope));
            var items = await inbox.ListAsync(20, unreadOnly: false, Ct);
            Assert.Contains(items, n => n.Id == notificationId && n.Title == "cross-cluster-callback-title");
        }

        // 2. Verify out-of-scope contract callback fails closed with 403 from Hosted OS /edge/sandbox/invoke.
        var denyAppKey = CodeOrchestrationScriptWrapper.AppKey("cross-cluster-deny-1");
        var denyCompiled = compiler.Compile(CodeOrchestrationScriptWrapper.BuildPackage(denyAppKey, "Scoped Deny", OutOfScopeNotificationScript));
        Assert.True(denyCompiled.Succeeded, string.Join("; ", denyCompiled.Errors));

        var denied = await ((ICodeSandbox)sandbox).ExecuteScopedCommandAsync(
            brain,
            denyCompiled.EnrichedPackage!,
            denyCompiled.AssemblyBytes,
            CodeOrchestrationScriptWrapper.Command,
            JsonSerializer.Serialize("{}"),
            allowedContracts: ["github"],
            cancellationToken: Ct);

        Assert.Equal("connected-client", denied.ExecutionTarget);
        Assert.Equal(fixture.LocalPc.MachineId, denied.ExecutedByMachineId);
        Assert.NotNull(denied.Error);
        Assert.Contains("outside the scoped AppToken", denied.Error, StringComparison.OrdinalIgnoreCase);

        // 3. Verify Local PC UnitHost temp directories were created and are cleaned up on bridge disposal.
        var localBridge = fixture.LocalPc.Services.GetRequiredService<HttpSandboxRuntimeBridge>();
        var unitHostTempDir = localBridge.ActiveUnitHostTempDirectory;
        var unitHostWorkDir = localBridge.ActiveUnitHostWorkingDirectory;
        Assert.False(string.IsNullOrWhiteSpace(unitHostTempDir));
        Assert.False(string.IsNullOrWhiteSpace(unitHostWorkDir));

        await localBridge.DisposeAsync();
        Assert.False(Directory.Exists(unitHostTempDir!));
        Assert.False(Directory.Exists(unitHostWorkDir!));
    }

    [Fact]
    [Trait("Category", "CrossClusterE2E")]
    public async Task Test03_ModeB_ConsentedFolders_SupportsCrud_EnforcesPathConfinement_AndLiveConsentRevocation()
    {
        await fixture.EnsureLinkedAsync(allowFolders: true, allowConsole: false, Ct);

        var brain = new BrainId(fixture.HostedOs.Scope);
        var hostedEdge = fixture.HostedOs.Services.GetRequiredService<EdgeAuthority>();
        using var caller = hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain));
        var remotePc = SiloNeurons.Get<IRemotePcResource>(fixture.HostedOs.Services, IRemotePcResource.Key(fixture.HostedOs.Scope));

        // 1. Discover connected PC with consented folder root "Projects".
        var pcs = await remotePc.ListConnectedPcsAsync(Ct);
        var pc = Assert.Single(pcs, n => n.MachineId == fixture.LocalPc.MachineId);
        Assert.True(pc.Online);
        Assert.True(pc.AllowsFolders);
        Assert.Contains("Projects", pc.ConsentedFolders, StringComparer.OrdinalIgnoreCase);

        // 2. Write, list, read, and stat inside consented root over real ServiceAssertion -> Relay -> DeviceWorker -> LocalPcResourceExecutor.
        const string expectedContent = "cross-cluster-mode-b-file-content";
        await remotePc.WriteFileTextAsync("Projects", "notes/hello.txt", expectedContent, fixture.LocalPc.MachineId, Ct);

        var entries = await remotePc.ListFolderAsync("Projects", "notes", fixture.LocalPc.MachineId, Ct);
        Assert.Contains(entries, e => e.Name == "hello.txt" && !e.IsDirectory);

        var readBack = await remotePc.ReadFileTextAsync("Projects", "notes/hello.txt", fixture.LocalPc.MachineId, Ct);
        Assert.Equal(expectedContent, readBack);

        var stat = await remotePc.StatPathAsync("Projects", "notes/hello.txt", fixture.LocalPc.MachineId, Ct);
        Assert.NotNull(stat);
        Assert.False(stat!.IsDirectory);
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedContent), stat.SizeBytes);

        // 3. Path traversal, rooted path, and non-consented root label MUST fail closed on the PC.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remotePc.ReadFileTextAsync("Projects", "../secret.txt", fixture.LocalPc.MachineId, Ct));

        var rootedTarget = OperatingSystem.IsWindows()
            ? Path.Combine(fixture.LocalPc.ConsentParentDir, "secret.txt")
            : "/etc/passwd";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remotePc.ReadFileTextAsync("Projects", rootedTarget, fixture.LocalPc.MachineId, Ct));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remotePc.ReadFileTextAsync("UnconsentedRoot", "hello.txt", fixture.LocalPc.MachineId, Ct));

        // 4. Live consent revocation on Local PC -> subsequent Hosted OS read fails closed immediately with consent-disabled.
        fixture.LocalPc.Services.GetRequiredService<IClientResourceConsent>().SetAllowWindowsFolderGateway(false);
        var revokedEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remotePc.ReadFileTextAsync("Projects", "notes/hello.txt", fixture.LocalPc.MachineId, Ct));
        Assert.Contains("consent-disabled", revokedEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "CrossClusterE2E")]
    public async Task Test04_ModeB_PersonOnlyConsole_EnforcesApprovalGate_Binding_SingleUse_AndReplayProtection()
    {
        await fixture.EnsureLinkedAsync(allowFolders: true, allowConsole: true, Ct);
        fixture.Relay.ResetCapturedResourceRequests();

        var brain = new BrainId(fixture.HostedOs.Scope);
        var hostedEdge = fixture.HostedOs.Services.GetRequiredService<EdgeAuthority>();
        var ownerPerson = new Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin);
        var agentCaller = hostedEdge.Agent(ownerPerson, brain, conversation: "ino-console-e2e");

        const string command = "echo hello-cross-cluster";
        const string rootLabel = "Projects";

        // 1. Agent requests console command -> MUST return approval_required with RequiresPerson=true and execute NOTHING on the PC.
        RemotePcShellOutcome initial;
        using (hostedEdge.Enter(agentCaller))
        {
            var remotePc = SiloNeurons.Get<IRemotePcResource>(fixture.HostedOs.Services, IRemotePcResource.Key(fixture.HostedOs.Scope));
            initial = await remotePc.RunConsoleCommandAsync(command, rootLabel: rootLabel, machineId: fixture.LocalPc.MachineId, cancellationToken: Ct);
        }

        Assert.Equal("approval_required", initial.Status);
        Assert.False(string.IsNullOrWhiteSpace(initial.PlanId));
        Assert.Equal(0, fixture.Relay.CapturedConsoleRequestCount);

        // 2. Verify ConfirmationPlan in Hosted OS inbox has RequiresPerson == true, then Person approves it.
        var planId = initial.PlanId!;
        using (hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain)))
        {
            var inbox = SiloNeurons.Get<IConfirmationInbox>(fixture.HostedOs.Services, IConfirmationInbox.Key(fixture.HostedOs.Scope));
            var pending = await inbox.Read();
            var request = Assert.Single(pending.Pending, p => p.Plan.Id == planId);
            Assert.True(request.Plan.RequiresPerson);
            await inbox.Resolve(planId, approve: true);
        }

        // 3. Mismatched command or mismatched rootLabel MUST be denied fail-closed without touching the PC.
        using (hostedEdge.Enter(agentCaller))
        {
            var remotePc = SiloNeurons.Get<IRemotePcResource>(fixture.HostedOs.Services, IRemotePcResource.Key(fixture.HostedOs.Scope));

            var badCommand = await remotePc.RunConsoleCommandAsync(
                "echo tampered-command",
                rootLabel: rootLabel,
                machineId: fixture.LocalPc.MachineId,
                approvedPlanId: planId,
                cancellationToken: Ct);
            Assert.Equal("denied", badCommand.Status);

            var badRoot = await remotePc.RunConsoleCommandAsync(
                command,
                rootLabel: "WrongRoot",
                machineId: fixture.LocalPc.MachineId,
                approvedPlanId: planId,
                cancellationToken: Ct);
            Assert.Equal("denied", badRoot.Status);
            Assert.Equal(0, fixture.Relay.CapturedConsoleRequestCount);

            // 4. Exact matching (machineId, command, rootLabel, approvedPlanId) executes ONCE on the Local PC.
            var executed = await remotePc.RunConsoleCommandAsync(
                command,
                rootLabel: rootLabel,
                machineId: fixture.LocalPc.MachineId,
                approvedPlanId: planId,
                cancellationToken: Ct);

            Assert.Equal("completed", executed.Status);
            Assert.Equal(0, executed.ExitCode);
            Assert.Contains("hello-cross-cluster", executed.StandardOutput ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(fixture.LocalPc.MachineId, executed.ExecutedByMachineId);
            Assert.Equal(1, fixture.Relay.CapturedConsoleRequestCount);

            // 5. Replaying the same approvedPlanId at Hosted OS fails closed.
            var replayedAtHost = await remotePc.RunConsoleCommandAsync(
                command,
                rootLabel: rootLabel,
                machineId: fixture.LocalPc.MachineId,
                approvedPlanId: planId,
                cancellationToken: Ct);
            Assert.Equal("denied", replayedAtHost.Status);
            Assert.Contains("already been consumed", replayedAtHost.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, fixture.Relay.CapturedConsoleRequestCount);
        }

        // 6. Direct replay of the captured signed PcResourceRequest against LocalPcResourceExecutor fails closed (person-approval-replay).
        var capturedRequest = fixture.Relay.LastCapturedConsoleRequest;
        Assert.NotNull(capturedRequest);
        var localExecutor = fixture.LocalPc.Services.GetRequiredService<ILocalPcResourceExecutor>();
        var replayResult = await localExecutor.ExecuteAsync(capturedRequest!, Ct);
        Assert.False(replayResult.Ok);
        Assert.Contains("person-approval-replay", replayResult.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "CrossClusterE2E")]
    public async Task Test05_DeviceRevocation_PreservesCustodyKey_AndFallsBackToTier2Within400msBudget()
    {
        await fixture.EnsureLinkedAsync(allowFolders: false, allowConsole: false, Ct);
        Assert.True(File.Exists(fixture.LocalPc.LinkBinPath));
        Assert.True(File.Exists(fixture.LocalPc.CustodyKeyPath));
        Assert.Equal(fixture.LocalPc.CustodySentinelContent, await File.ReadAllTextAsync(fixture.LocalPc.CustodyKeyPath, Ct));

        var compiler = new AppPackageCompiler();
        var brain = new BrainId(fixture.HostedOs.Scope);
        var sandbox = fixture.HostedOs.Services.GetRequiredService<ISandboxRuntimeBridge>();
        var hostedRouter = fixture.HostedOs.Services.GetRequiredService<IConnectedComputeRouter>();

        var appKey = CodeOrchestrationScriptWrapper.AppKey("cross-cluster-fallback");
        var compiled = compiler.Compile(CodeOrchestrationScriptWrapper.BuildPackage(appKey, "Fallback Compute", PureComputeScript));
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));

        // 1. Simulate unresponsive/slow Local PC while still advertised online -> Hosted OS detects unavailability within <400ms budget and falls back to Tier 2!
        fixture.Relay.SimulateUnresponsiveWorker = true;
        try
        {
            var probeAndFallbackSw = Stopwatch.StartNew();
            var fallbackDecision = await hostedRouter.TryExecuteOnConnectedClientAsync(
                brain,
                new ConnectedSandboxRequest(
                    JobId: "unresponsive-budget-check",
                    BrainId: brain.Value,
                    AppKey: appKey,
                    Version: compiled.EnrichedPackage!.Version,
                    Digest: compiled.EnrichedPackage.Digest,
                    AssemblyBase64: Convert.ToBase64String(compiled.AssemblyBytes),
                    Command: CodeOrchestrationScriptWrapper.Command,
                    PayloadJson: JsonSerializer.Serialize("""{"count":14}"""),
                    StateJson: null,
                    SettingsJson: null,
                    KernelCallbackUrl: null,
                    AppToken: "unused-budget-probe",
                    AllowedContracts: []),
                Ct);
            probeAndFallbackSw.Stop();

            Assert.Null(fallbackDecision);
            // AvailabilityTimeoutMs is configured to 250ms on Hosted OS; allow generous CI jitter margin while staying strictly under 1500ms.
            Assert.True(
                probeAndFallbackSw.Elapsed < TimeSpan.FromMilliseconds(1500),
                $"Expected fast (<400ms configured) fallback detection, but took {probeAndFallbackSw.ElapsedMilliseconds}ms.");

            // Full ICodeSandbox.ExecuteScopedCommandAsync while PC is unresponsive must fall back cleanly to Tier 2.
            var unresponsiveFallback = await ((ICodeSandbox)sandbox).ExecuteScopedCommandAsync(
                brain,
                compiled.EnrichedPackage!,
                compiled.AssemblyBytes,
                CodeOrchestrationScriptWrapper.Command,
                JsonSerializer.Serialize("""{"count":14}"""),
                allowedContracts: [],
                cancellationToken: Ct);

            Assert.True(unresponsiveFallback.Handled, unresponsiveFallback.Error);
            Assert.Null(unresponsiveFallback.Error);
            Assert.Equal("local-unit-host", unresponsiveFallback.ExecutionTarget);
            Assert.Null(unresponsiveFallback.ExecutedByMachineId);
            using var fallbackDoc = ParseOrchestrationResult(unresponsiveFallback.ResultJson!);
            Assert.Equal("succeeded", fallbackDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal("Computed:42", fallbackDoc.RootElement.GetProperty("summary").GetString());
        }
        finally
        {
            fixture.Relay.SimulateUnresponsiveWorker = false;
        }

        // 2. Revoke the PC on Hosted OS -> next DeviceWorker poll gets 401 -> clears link.bin while keeping custody.key intact!
        var hostedEdge = fixture.HostedOs.Services.GetRequiredService<EdgeAuthority>();
        using (hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain)))
        {
            var enrollment = SiloNeurons.Get<IDeviceEnrollment>(fixture.HostedOs.Services, IDeviceEnrollment.Key(fixture.HostedOs.Scope));
            await enrollment.Revoke(fixture.LocalPc.MachineId);
        }

        var revokedDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < revokedDeadline && File.Exists(fixture.LocalPc.LinkBinPath))
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(File.Exists(fixture.LocalPc.LinkBinPath));
        Assert.True(File.Exists(fixture.LocalPc.CustodyKeyPath));
        Assert.Equal(fixture.LocalPc.CustodySentinelContent, await File.ReadAllTextAsync(fixture.LocalPc.CustodyKeyPath, Ct));

        var revokedStatus = await fixture.GetLocalPcLinkStatusAsync(Ct);
        Assert.Equal(LinkState.Revoked, revokedStatus.State);

        // 3. With Local PC revoked, Hosted OS ICodeSandbox.ExecuteScopedCommandAsync falls back cleanly to Tier 2 and succeeds!
        var tier2Result = await ((ICodeSandbox)sandbox).ExecuteScopedCommandAsync(
            brain,
            compiled.EnrichedPackage!,
            compiled.AssemblyBytes,
            CodeOrchestrationScriptWrapper.Command,
            JsonSerializer.Serialize("""{"count":14}"""),
            allowedContracts: [],
            cancellationToken: Ct);

        Assert.True(tier2Result.Handled, tier2Result.Error);
        Assert.Null(tier2Result.Error);
        Assert.Equal("local-unit-host", tier2Result.ExecutionTarget);
        Assert.Null(tier2Result.ExecutedByMachineId);
        using var tier2Doc = ParseOrchestrationResult(tier2Result.ResultJson!);
        Assert.Equal("succeeded", tier2Doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Computed:42", tier2Doc.RootElement.GetProperty("summary").GetString());
    }

    private static JsonDocument ParseOrchestrationResult(string resultJson)
    {
        var doc = JsonDocument.Parse(resultJson);
        if (doc.RootElement.ValueKind == JsonValueKind.String)
        {
            var inner = doc.RootElement.GetString() ?? "{}";
            doc.Dispose();
            return JsonDocument.Parse(inner);
        }

        return doc;
    }
}

public sealed class CrossClusterConnectedComputeFixture : IAsyncLifetime
{
    public const string HostedOsIssuer = "os.test.intochat.io";
    public const string HostedOsAudience = "https://os.test.intochat.io";
    public const string RelayIssuer = "cloud.relay.test";

    private readonly ECDsa _hostedOsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _relayKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal CircleRelayTestServer Relay { get; private set; } = null!;
    internal OsClusterInstance HostedOs { get; private set; } = null!;
    internal OsClusterInstance LocalPc { get; private set; } = null!;
    internal HttpClient Http { get; } = new();

    public async ValueTask InitializeAsync()
    {
        // 1. Start Real HTTP Circle Relay Server first so Hosted OS can be configured with its ephemeral loopback origin.
        Relay = await CircleRelayTestServer.StartAsync(
            relayIssuer: RelayIssuer,
            relayKey: _relayKey,
            hostedOsIssuer: HostedOsIssuer,
            hostedOsPublicSpki: _hostedOsKey.ExportSubjectPublicKeyInfo(),
            hostedOsAudience: HostedOsAudience,
            CancellationToken.None);

        var devicesSection = DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection("devices");
        var sandboxSection = DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(SandboxOptions.SectionName);

        // 2. Start Hosted Cloud OS Cluster (real Kestrel + Orleans silo).
        HostedOs = await OsClusterInstance.StartAsync(
            name: "hosted-cloud-os",
            configure: builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [$"{devicesSection}:Ollama:Enabled"] = "false",
                    [$"{devicesSection}:FoundryLocal:Enabled"] = "false",
                    [$"{devicesSection}:SherpaOnnx:Enabled"] = "false",
                    [$"{devicesSection}:Compute:AvailabilityTimeoutMs"] = "300",
                    [$"{sandboxSection}:TokenSigningMaterial"] = "hosted-cloud-os-e2e-signing-key-0123456789abcdef",
                    [$"{sandboxSection}:EnableLocalSandbox"] = "true",
                    [AuthKeys.ServiceSigner + ":Issuer"] = HostedOsIssuer,
                    [AuthKeys.ServiceSigner + ":Audience"] = Relay.Origin,
                    [AuthKeys.ServiceSigner + ":ServiceUrl"] = Relay.Origin,
                    [AuthKeys.ServiceSigner + ":PrivateKey"] = Convert.ToBase64String(_hostedOsKey.ExportPkcs8PrivateKey()),
                    [AuthKeys.TrustedIssuers + ":0:Name"] = RelayIssuer,
                    [AuthKeys.TrustedIssuers + ":0:PublicKey"] = Convert.ToBase64String(_relayKey.ExportSubjectPublicKeyInfo()),
                    [AuthKeys.TrustedIssuers + ":0:Audience"] = HostedOsAudience,
                    [AuthKeys.TrustedIssuers + ":0:Services"] = "true",
                });
            },
            createConsentedFolder: false,
            CancellationToken.None);

        Relay.SetHostedOsOrigin(HostedOs.BaseAddress.AbsoluteUri.TrimEnd('/'));

        // 3. Start Local Windows/Desktop PC OS Cluster (real Kestrel + Orleans silo + DeviceWorker + real UnitHost + LocalPcResourceExecutor).
        LocalPc = await OsClusterInstance.StartAsync(
            name: "local-pc-os",
            configure: builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [$"{devicesSection}:Ollama:Enabled"] = "false",
                    [$"{devicesSection}:FoundryLocal:Enabled"] = "false",
                    [$"{devicesSection}:SherpaOnnx:Enabled"] = "false",
                    [$"{devicesSection}:Compute:HeartbeatIntervalSeconds"] = "1",
                    [$"{devicesSection}:Compute:AllowClientCodeSandbox"] = "true",
                    [$"{sandboxSection}:TokenSigningMaterial"] = "local-pc-only-different-signing-key-fedcba9876543210",
                    [$"{sandboxSection}:AllowLoopbackKernelCallback"] = "true",
                    [$"{sandboxSection}:EnableLocalSandbox"] = "true",
                });
            },
            createConsentedFolder: true,
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (LocalPc is not null)
        {
            await LocalPc.DisposeAsync();
        }

        if (HostedOs is not null)
        {
            await HostedOs.DisposeAsync();
        }

        if (Relay is not null)
        {
            await Relay.DisposeAsync();
        }

        _hostedOsKey.Dispose();
        _relayKey.Dispose();
    }

    internal async Task<LinkDevicePayload> RequestLinkDeviceUriAsync(CancellationToken ct)
    {
        using var res = await Http.GetAsync(
            $"{Relay.Origin}/account/link-device?brain={Uri.EscapeDataString(HostedOs.Scope)}",
            ct);
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<LinkDevicePayload>(JsonSerializerOptions.Web, ct);
        return payload ?? throw new InvalidDataException("Empty /account/link-device response.");
    }

    internal async Task<HttpResponseMessage> SendLocalPcAsPersonAsync(
        HttpMethod method,
        string path,
        string? bodyJson,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add(PresenceProof.HeaderName, LocalPc.PresenceToken);
        if (bodyJson is not null)
        {
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        return await LocalPc.Client.SendAsync(req, ct);
    }

    internal async Task<LinkStatus> GetLocalPcLinkStatusAsync(CancellationToken ct)
    {
        using var res = await SendLocalPcAsPersonAsync(
            HttpMethod.Get,
            $"/brains/{LocalPc.Scope}/devices/link/status",
            bodyJson: null,
            ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(json, DeviceWireJson.Default.LinkStatus)
            ?? throw new InvalidDataException("Empty link status response.");
    }

    internal async Task EnsureUnlinkedAsync(CancellationToken ct)
    {
        using (var leaveRes = await SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{LocalPc.Scope}/devices/link/leave",
            bodyJson: null,
            ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, leaveRes.StatusCode);
        }

        using (var cancelRes = await SendLocalPcAsPersonAsync(
            HttpMethod.Post,
            $"/brains/{LocalPc.Scope}/devices/link/pending/cancel",
            bodyJson: null,
            ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelRes.StatusCode);
        }

        var hostedBrain = new BrainId(HostedOs.Scope);
        var hostedEdge = HostedOs.Services.GetRequiredService<EdgeAuthority>();
        using (hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, hostedBrain)))
        {
            var enrollment = SiloNeurons.Get<IDeviceEnrollment>(HostedOs.Services, IDeviceEnrollment.Key(HostedOs.Scope));
            await enrollment.Revoke(LocalPc.MachineId);
        }

        Relay.ClearMachine(LocalPc.MachineId);
        await HostedOs.Services.GetRequiredService<IConnectedComputeRouter>().MarkOfflineAsync(hostedBrain, LocalPc.MachineId, ct);
    }

    internal async Task EnsureLinkedAsync(bool allowFolders, bool allowConsole, CancellationToken ct)
    {
        var status = await GetLocalPcLinkStatusAsync(ct);
        if (status.State != LinkState.Connected || !File.Exists(LocalPc.LinkBinPath))
        {
            // Ensure Hosted OS doesn't hold a stale non-revoked entry for the same machineId before re-enrolling.
            var hostedBrain = new BrainId(HostedOs.Scope);
            var hostedEdge = HostedOs.Services.GetRequiredService<EdgeAuthority>();
            using (hostedEdge.Enter(hostedEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, hostedBrain)))
            {
                var enrollment = SiloNeurons.Get<IDeviceEnrollment>(HostedOs.Services, IDeviceEnrollment.Key(HostedOs.Scope));
                await enrollment.Revoke(LocalPc.MachineId);
            }

            Relay.ClearMachine(LocalPc.MachineId);

            var linkDevice = await RequestLinkDeviceUriAsync(ct);
            using (var stage = await SendLocalPcAsPersonAsync(
                HttpMethod.Post,
                $"/brains/{LocalPc.Scope}/devices/link/pending",
                JsonSerializer.Serialize(new { uri = linkDevice.Uri }),
                ct))
            {
                stage.EnsureSuccessStatusCode();
            }

            using var confirm = await SendLocalPcAsPersonAsync(
                HttpMethod.Post,
                $"/brains/{LocalPc.Scope}/devices/link/pending/confirm",
                bodyJson: null,
                ct);
            confirm.EnsureSuccessStatusCode();
        }

        var localBrain = new BrainId(LocalPc.Scope);
        var localEdge = LocalPc.Services.GetRequiredService<EdgeAuthority>();
        using (localEdge.Enter(localEdge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, localBrain)))
        {
            var deviceLink = SiloNeurons.Get<IDeviceLink>(LocalPc.Services, IDeviceLink.Key(LocalPc.Scope));
            await deviceLink.SetAllowClientCodeSandbox(true);
            await deviceLink.SetAllowWindowsFolderGateway(allowFolders);
            await deviceLink.SetAllowConsole(allowConsole);
        }

        var found = await WaitForHostedNodeAsync(
            node => node.MachineId == LocalPc.MachineId
                && node.Status == ComputeNodeStatus.Online
                && node.IsEligibleForSovereignCompute
                && node.AllowWindowsFolderGateway == allowFolders
                && node.AllowConsole == allowConsole
                && (!allowFolders || (node.ConsentedFolderLabels?.Contains("Projects", StringComparer.OrdinalIgnoreCase) ?? false)),
            ct);
        Assert.NotNull(found);

        // Ensure DeviceWorker is actively polling (e.g. if a previous test revoked the link and DeviceWorker
        // was briefly in its 5s ErrorDelay backoff).
        var pollsBefore = Relay.SandboxPollCount;
        var pollDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < pollDeadline && Relay.SandboxPollCount == pollsBefore)
        {
            await Task.Delay(50, ct);
        }

        Assert.True(Relay.SandboxPollCount > pollsBefore, "DeviceWorker did not resume polling sandbox/next after link.");
    }

    internal async Task<ConnectedComputeNode?> WaitForHostedNodeAsync(
        Func<ConnectedComputeNode, bool> predicate,
        CancellationToken ct)
    {
        var brain = new BrainId(HostedOs.Scope);
        var router = HostedOs.Services.GetRequiredService<IConnectedComputeRouter>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Force a fresh relay poll rather than waiting out the 5s relay cache window.
            await router.MarkOfflineAsync(brain, LocalPc.MachineId, ct);
            var nodes = await router.ListNodesAsync(brain, ct);
            var match = nodes.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(75, ct);
        }

        return null;
    }

    internal sealed record LinkDevicePayload(string Uri, string Code, DateTimeOffset ExpiresAt);
}

internal sealed class OsClusterInstance : IAsyncDisposable
{
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(30);

    private readonly WebApplication _app;
    private readonly IDisposable _ports;
    private readonly string _homeDir;
    private readonly ConcurrentQueue<(string Path, bool HasAppToken)> _sandboxEdgeHits;

    private OsClusterInstance(
        WebApplication app,
        HttpClient client,
        IDisposable ports,
        string homeDir,
        string scope,
        string presenceToken,
        string machineId,
        string custodyKeyPath,
        string custodySentinelContent,
        string consentParentDir,
        string consentedProjectsDir,
        ConcurrentQueue<(string Path, bool HasAppToken)> sandboxEdgeHits)
    {
        _app = app;
        Client = client;
        _ports = ports;
        _homeDir = homeDir;
        Scope = scope;
        PresenceToken = presenceToken;
        MachineId = machineId;
        CustodyKeyPath = custodyKeyPath;
        CustodySentinelContent = custodySentinelContent;
        ConsentParentDir = consentParentDir;
        ConsentedProjectsDir = consentedProjectsDir;
        _sandboxEdgeHits = sandboxEdgeHits;
    }

    public IServiceProvider Services => _app.Services;
    public HttpClient Client { get; }
    public Uri BaseAddress => Client.BaseAddress!;
    public string Scope { get; }
    public string PresenceToken { get; }
    public string MachineId { get; }
    public string LinkBinPath => Path.Combine(_homeDir, "devices", "link.bin");
    public string CustodyKeyPath { get; }
    public string CustodySentinelContent { get; }
    public string ConsentParentDir { get; }
    public string ConsentedProjectsDir { get; }
    public IReadOnlyCollection<(string Path, bool HasAppToken)> SandboxEdgeHits => _sandboxEdgeHits;

    public void ClearSandboxEdgeHits()
    {
        while (_sandboxEdgeHits.TryDequeue(out _))
        {
        }
    }

    public static async Task<OsClusterInstance> StartAsync(
        string name,
        Action<WebApplicationBuilder> configure,
        bool createConsentedFolder,
        CancellationToken ct)
    {
        var homeDir = Path.Combine(Path.GetTempPath(), $"intochat-xcluster-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(homeDir, "run"));
        Directory.CreateDirectory(Path.Combine(homeDir, "devices"));

        var custodySentinel = $"custody-sentinel-{Guid.NewGuid():N}";
        var custodyKeyPath = Path.Combine(homeDir, "custody.key");
        await File.WriteAllTextAsync(custodyKeyPath, custodySentinel, ct);

        var consentParentDir = Path.Combine(homeDir, "consented-area");
        var consentedProjectsDir = Path.Combine(consentParentDir, "Projects");
        if (createConsentedFolder)
        {
            Directory.CreateDirectory(consentedProjectsDir);
            await File.WriteAllTextAsync(Path.Combine(consentParentDir, "secret.txt"), "top-secret-outside-consented-root", ct);
        }

        var rawPresence = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(Path.Combine(homeDir, "run", "presence"), rawPresence, ct);

        var ports = PortReservation.ReservePair();
        WebApplication? app = null;
        HttpClient? client = null;
        var edgeHits = new ConcurrentQueue<(string Path, bool HasAppToken)>();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
                ContentRootPath = Path.GetTempPath(),
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                [IntoChatOSNames.HomeKey] = homeDir,
                [IntoChatOSNames.ShellEnabledKey] = "false",
                [IntoChatOSNames.FrontEnabledKey] = "false",
                [AuthKeys.Posture] = "Open",
                [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
            });

            LocalRunDefaults.Apply(builder.Configuration);
            configure(builder);
            builder.AddIntoChatOSServer(server => server.AddOperatingSystem(OperatingSystemModules.InProcessModules));
            // Each OS cluster (Hosted Cloud OS vs Local Desktop PC) has its own distinct machine identity.
            builder.Services.Replace(ServiceDescriptor.Singleton(new MachineId($"{name}-{Guid.NewGuid():N}"[..24])));
            if (createConsentedFolder)
            {
                builder.Services.Replace(ServiceDescriptor.Singleton<IConsentedFolderRootProvider>(
                    new StaticConsentedFolderRootProvider([consentedProjectsDir])));
            }

            app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/edge/sandbox", StringComparison.OrdinalIgnoreCase))
                {
                    edgeHits.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Headers.ContainsKey("X-App-Token")));
                }

                await next();
            });
            app.MapIntoChatOSServer();
            await app.StartAsync(ct);

            var baseUrl = app.Urls.First();
            client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
            var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
            var proof = app.Services.GetRequiredService<IPresenceProof>();
            var machineId = app.Services.GetRequiredService<MachineId>().Value;

            var deadline = DateTimeOffset.UtcNow + ReadyBudget;
            var ready = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var res = await client.GetAsync("/ready", ct);
                    if (res.IsSuccessStatusCode)
                    {
                        ready = true;
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(75, ct);
            }

            if (!ready)
            {
                throw new TimeoutException($"OsClusterInstance '{name}' at '{baseUrl}' did not become ready within {ReadyBudget}.");
            }

            return new OsClusterInstance(
                app,
                client,
                ports,
                homeDir,
                scope,
                proof.Token,
                machineId,
                custodyKeyPath,
                custodySentinel,
                consentParentDir,
                consentedProjectsDir,
                edgeHits);
        }
        catch
        {
            client?.Dispose();
            if (app is not null)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(stopCts.Token);
                }
                catch
                {
                }

                await app.DisposeAsync();
            }

            ports.Dispose();
            DigitalBrain.Testing.TempDirectory.DeleteIfPresent(homeDir);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Client.Dispose();
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _app.StopAsync(stopCts.Token);
            }
            finally
            {
                await _app.DisposeAsync();
            }
        }
        finally
        {
            _ports.Dispose();
            DigitalBrain.Testing.TempDirectory.DeleteIfPresent(_homeDir);
        }
    }

    private sealed class StaticConsentedFolderRootProvider(string[] roots) : IConsentedFolderRootProvider
    {
        public Task<string[]> GetConsentedRootsAsync(string brainId, CancellationToken cancellationToken = default)
            => Task.FromResult(roots);
    }
}

/// <summary>
/// Real loopback Kestrel HTTP Circle Relay Server implementing the locked Slice 1 wire contract
/// (intochat/cloud#33): ES256 ServiceAssertion verification + jti replay cache on caller routes,
/// Bearer + P-256 X-Machine-Proof + nonce replay + Hosted OS /live check on worker routes.
/// </summary>
internal sealed class CircleRelayTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _hostedClient = new();
    private readonly string _relayIssuer;
    private readonly ECDsa _relayKey;
    private readonly string _hostedOsIssuer;
    private readonly ECDsa _hostedOsPublicKey;
    private readonly string _hostedOsAudience;
    private readonly ConcurrentDictionary<string, byte> _usedServiceJtis = new(StringComparer.Ordinal);
    private readonly InMemoryNonceCache _machineProofNonces = new();
    private readonly ConcurrentDictionary<string, EnrolledMachine> _machinesByAccessToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EnrolledMachine> _machinesById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ComputeCapabilityAdvertisement> _capabilitiesByMachineId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingJob<ConnectedSandboxRequest, ConnectedSandboxResponse>> _sandboxJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingJob<PcResourceRequest, PcResourceResponse>> _resourceJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingJob<ConnectedSandboxRequest, ConnectedSandboxResponse>>> _sandboxQueues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingJob<PcResourceRequest, PcResourceResponse>>> _resourceQueues = new(StringComparer.Ordinal);

    private string _hostedOsOrigin = string.Empty;
    private int _capabilityReportsReceived;
    private int _serviceAssertionCallsVerified;
    private int _capturedConsoleRequestCount;
    private int _sandboxPollCount;
    private string _lastSandboxDispatchDiag = "none";
    private PcResourceRequest? _lastCapturedConsoleRequest;

    private CircleRelayTestServer(
        WebApplication app,
        string origin,
        string relayIssuer,
        ECDsa relayKey,
        string hostedOsIssuer,
        ECDsa hostedOsPublicKey,
        string hostedOsAudience)
    {
        _app = app;
        Origin = origin;
        _relayIssuer = relayIssuer;
        _relayKey = relayKey;
        _hostedOsIssuer = hostedOsIssuer;
        _hostedOsPublicKey = hostedOsPublicKey;
        _hostedOsAudience = hostedOsAudience;
    }

    public string Origin { get; }
    public bool SimulateUnresponsiveWorker { get; set; }
    public int CapabilityReportsReceived => Volatile.Read(ref _capabilityReportsReceived);
    public int ServiceAssertionCallsVerified => Volatile.Read(ref _serviceAssertionCallsVerified);
    public int CapturedConsoleRequestCount => Volatile.Read(ref _capturedConsoleRequestCount);
    public int SandboxPollCount => Volatile.Read(ref _sandboxPollCount);
    public string LastSandboxDispatchDiag => Volatile.Read(ref _lastSandboxDispatchDiag);
    public PcResourceRequest? LastCapturedConsoleRequest => Volatile.Read(ref _lastCapturedConsoleRequest);

    public void SetHostedOsOrigin(string hostedOsOrigin) => _hostedOsOrigin = hostedOsOrigin.TrimEnd('/');

    public void ResetCapturedResourceRequests()
    {
        Interlocked.Exchange(ref _capturedConsoleRequestCount, 0);
        Volatile.Write(ref _lastCapturedConsoleRequest, null);
    }

    public void ClearMachine(string machineId)
    {
        if (_machinesById.TryRemove(machineId, out var removed))
        {
            _machinesByAccessToken.TryRemove(removed.AccessToken, out _);
        }

        _capabilitiesByMachineId.TryRemove(machineId, out _);
        _sandboxQueues.TryRemove(machineId, out _);
        _resourceQueues.TryRemove(machineId, out _);
    }

    public static async Task<CircleRelayTestServer> StartAsync(
        string relayIssuer,
        ECDsa relayKey,
        string hostedOsIssuer,
        byte[] hostedOsPublicSpki,
        string hostedOsAudience,
        CancellationToken ct)
    {
        var hostedPublicKey = ECDsa.Create();
        hostedPublicKey.ImportSubjectPublicKeyInfo(hostedOsPublicSpki, out _);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        CircleRelayTestServer? server = null;

        app.MapGet("/account/link-device", http => server!.HandleLinkDeviceAsync(http));
        app.MapPost(CircleRoutes.Enroll, http => server!.HandleEnrollAsync(http));
        app.MapPost(CircleRoutes.Refresh, http => server!.HandleRefreshAsync(http));
        app.MapGet(CircleRoutes.Status, http => server!.HandleStatusAsync(http));
        app.MapPost(CircleRoutes.Leave, http => server!.HandleLeaveAsync(http));

        // Worker routes (strictly Bearer + X-Machine-Proof + /live; ServiceAssertion -> 401).
        app.MapPut(DeviceRoutes.Models, http => server!.HandleWorkerModelsAsync(http));
        app.MapPut(DeviceRoutes.ComputeCapability, http => server!.HandleWorkerCapabilityAsync(http));
        app.MapGet(DeviceRoutes.SandboxNext, http => server!.HandleWorkerSandboxNextAsync(http));
        app.MapPost(DeviceRoutes.SandboxResult, (HttpContext http, string jobId) => server!.HandleWorkerSandboxResultAsync(http, jobId));
        app.MapGet(DeviceRoutes.ResourceNext, http => server!.HandleWorkerResourceNextAsync(http));
        app.MapPost(DeviceRoutes.ResourceResult, (HttpContext http, string jobId) => server!.HandleWorkerResourceResultAsync(http, jobId));
        app.MapGet(DeviceRoutes.Next, http => server!.HandleWorkerChatNextAsync(http));

        // Caller routes (strictly ES256 ServiceAssertion with purpose/op == "circle-compute-dispatch").
        app.MapGet(DeviceRoutes.ComputeNodes, http => server!.HandleCallerComputeNodesAsync(http));
        app.MapPost(DeviceRoutes.SandboxDispatch, http => server!.HandleCallerSandboxDispatchAsync(http));
        app.MapPost(DeviceRoutes.ResourceDispatch, http => server!.HandleCallerResourceDispatchAsync(http));

        await app.StartAsync(ct);
        var origin = app.Urls.First().TrimEnd('/');
        server = new CircleRelayTestServer(app, origin, relayIssuer, relayKey, hostedOsIssuer, hostedPublicKey, hostedOsAudience);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        _hostedClient.Dispose();
        _hostedOsPublicKey.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleLinkDeviceAsync(HttpContext http)
    {
        var brainId = http.Request.Query["brain"].ToString();
        if (string.IsNullOrWhiteSpace(brainId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new MintRequest(brainId), EnrollmentEdgeJson.Default.MintRequest);
        using var res = await SendToHostedOsAssertedAsync(EnrollmentEdge.MintOperation, HttpMethod.Post, EnrollmentEdge.Mint, body, http.RequestAborted);
        if (!res.IsSuccessStatusCode)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            return;
        }

        var mintJson = await res.Content.ReadAsStringAsync(http.RequestAborted);
        var minted = JsonSerializer.Deserialize(mintJson, EnrollmentEdgeJson.Default.MintResponse)!;
        var enrollUri = DeviceEnrollUri.Build(new Uri(Origin, UriKind.Absolute), new Code(minted.Code));

        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(
            new CrossClusterConnectedComputeFixture.LinkDevicePayload(enrollUri.AbsoluteUri, minted.Code, minted.ExpiresAt),
            JsonSerializerOptions.Web,
            http.RequestAborted);
    }

    private async Task HandleEnrollAsync(HttpContext http)
    {
        var req = await JsonSerializer.DeserializeAsync(http.Request.Body, DeviceWireJson.Default.EnrollRequest, http.RequestAborted);
        if (req is null || string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.MachinePublicKeySpki))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var spki = Base64Url.DecodeFromChars(req.MachinePublicKeySpki);
        var thumbprint = DeviceKeys.Thumbprint(spki);

        var redeemBody = JsonSerializer.SerializeToUtf8Bytes(
            new RedeemRequest(req.Code, req.MachineId, thumbprint, req.MachineName),
            EnrollmentEdgeJson.Default.RedeemRequest);
        using var res = await SendToHostedOsAssertedAsync(EnrollmentEdge.RedeemOperation, HttpMethod.Post, EnrollmentEdge.Redeem, redeemBody, http.RequestAborted);
        if (!res.IsSuccessStatusCode)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var redeemJson = await res.Content.ReadAsStringAsync(http.RequestAborted);
        var redeem = JsonSerializer.Deserialize(redeemJson, EnrollmentEdgeJson.Default.RedeemResponse)!;
        var outcome = redeem.ToOutcome();
        if (outcome.Result is not (EnrollmentResult.Accepted or EnrollmentResult.Repeat))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var accessToken = "acc-" + Guid.NewGuid().ToString("N");
        var refreshToken = "ref-" + Guid.NewGuid().ToString("N");
        var credId = "cred-" + Guid.NewGuid().ToString("N")[..12];

        var machine = new EnrolledMachine(req.MachineId, outcome.Brain, outcome.DeviceName, spki, thumbprint, accessToken, refreshToken);
        if (_machinesById.TryRemove(req.MachineId, out var prev))
        {
            _machinesByAccessToken.TryRemove(prev.AccessToken, out _);
        }

        _machinesById[req.MachineId] = machine;
        _machinesByAccessToken[accessToken] = machine;

        var enrollResponse = new EnrollResponse(
            MachineCredentialId: credId,
            AccessToken: accessToken,
            AccessExpiresAt: now.AddHours(1),
            RefreshToken: refreshToken,
            RefreshExpiresAt: now.AddDays(30),
            Circle: new CircleInfo(outcome.Brain, "Test Circle", "owner"),
            PendingAccount: false,
            Endpoints: new CircleEndpoints(
                CircleRoutes.JournalFor(outcome.Brain),
                CircleRoutes.Store,
                CircleRoutes.FilesFor(outcome.Brain, "")));

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, enrollResponse, DeviceWireJson.Default.EnrollResponse, http.RequestAborted);
    }

    private async Task HandleRefreshAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: true);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var newAccess = "acc-" + Guid.NewGuid().ToString("N");
        var newRefresh = "ref-" + Guid.NewGuid().ToString("N");
        var updated = machine with { AccessToken = newAccess, RefreshToken = newRefresh };
        _machinesByAccessToken.TryRemove(machine.AccessToken, out _);
        _machinesById[machine.MachineId] = updated;
        _machinesByAccessToken[newAccess] = updated;

        var response = new EnrollResponse(
            MachineCredentialId: "cred-refreshed",
            AccessToken: newAccess,
            AccessExpiresAt: now.AddHours(1),
            RefreshToken: newRefresh,
            RefreshExpiresAt: now.AddDays(30),
            Circle: new CircleInfo(machine.BrainId, "Test Circle", "owner"),
            PendingAccount: false,
            Endpoints: new CircleEndpoints(
                CircleRoutes.JournalFor(machine.BrainId),
                CircleRoutes.Store,
                CircleRoutes.FilesFor(machine.BrainId, "")));

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, response, DeviceWireJson.Default.EnrollResponse, http.RequestAborted);
    }

    private async Task HandleStatusAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: false);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var live = await CheckHostedOsLiveAsync(machine, http.RequestAborted);
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            new CircleStatusResponse(machine.BrainId, "Test Circle", "owner", PendingAccount: false, Revoked: !live, machine.MachineId),
            DeviceWireJson.Default.CircleStatusResponse,
            http.RequestAborted);
    }

    private async Task HandleLeaveAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: false);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ClearMachine(machine.MachineId);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerModelsAsync(HttpContext http)
    {
        if (await AuthenticateWorkerAsync(http, checkLive: true) is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerCapabilityAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: true);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var cap = await JsonSerializer.DeserializeAsync(http.Request.Body, DeviceWireJson.Default.ComputeCapabilityAdvertisement, http.RequestAborted);
        if (cap is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        _capabilitiesByMachineId[machine.MachineId] = cap with
        {
            MachineId = machine.MachineId,
            KeyThumbprint = machine.Thumbprint,
        };
        Interlocked.Increment(ref _capabilityReportsReceived);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerSandboxNextAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: true);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Interlocked.Increment(ref _sandboxPollCount);

        if (SimulateUnresponsiveWorker)
        {
            await Task.Delay(50, http.RequestAborted);
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var queue = _sandboxQueues.GetOrAdd(machine.MachineId, static _ => new ConcurrentQueue<PendingJob<ConnectedSandboxRequest, ConnectedSandboxResponse>>());
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(30);
        while (DateTimeOffset.UtcNow < deadline && !http.RequestAborted.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var job))
            {
                job.Claimed.TrySetResult(true);
                http.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(http.Response.Body, job.Request, DeviceWireJson.Default.ConnectedSandboxRequest, http.RequestAborted);
                return;
            }

            await Task.Delay(5, http.RequestAborted);
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerSandboxResultAsync(HttpContext http, string jobId)
    {
        if (await AuthenticateWorkerAsync(http, checkLive: true) is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var result = await JsonSerializer.DeserializeAsync(http.Request.Body, DeviceWireJson.Default.ConnectedSandboxResponse, http.RequestAborted);
        if (result is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (_sandboxJobs.TryRemove(jobId, out var pending))
        {
            pending.Result.TrySetResult(result);
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerResourceNextAsync(HttpContext http)
    {
        var machine = await AuthenticateWorkerAsync(http, checkLive: true);
        if (machine is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var queue = _resourceQueues.GetOrAdd(machine.MachineId, static _ => new ConcurrentQueue<PendingJob<PcResourceRequest, PcResourceResponse>>());
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(30);
        while (DateTimeOffset.UtcNow < deadline && !http.RequestAborted.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var job))
            {
                if (string.Equals(job.Request.Capability, ClientComputeConsentPolicy.ConsoleCapability, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _capturedConsoleRequestCount);
                    Volatile.Write(ref _lastCapturedConsoleRequest, job.Request);
                }

                job.Claimed.TrySetResult(true);
                http.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(http.Response.Body, job.Request, DeviceWireJson.Default.PcResourceRequest, http.RequestAborted);
                return;
            }

            await Task.Delay(5, http.RequestAborted);
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerResourceResultAsync(HttpContext http, string jobId)
    {
        if (await AuthenticateWorkerAsync(http, checkLive: true) is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var result = await JsonSerializer.DeserializeAsync(http.Request.Body, DeviceWireJson.Default.PcResourceResponse, http.RequestAborted);
        if (result is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (_resourceJobs.TryRemove(jobId, out var pending))
        {
            pending.Result.TrySetResult(result);
        }

        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleWorkerChatNextAsync(HttpContext http)
    {
        if (await AuthenticateWorkerAsync(http, checkLive: true) is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await Task.Delay(15, http.RequestAborted);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleCallerComputeNodesAsync(HttpContext http)
    {
        var auth = await AuthenticateCallerServiceAssertionAsync(http);
        if (auth is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var brain = http.Request.Query["brain"].ToString();
        if (!string.Equals(brain, auth.Value.Claims.Subject, StringComparison.Ordinal))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var nodes = new List<ComputeCapabilityAdvertisement>();
        foreach (var machine in _machinesById.Values.Where(m => string.Equals(m.BrainId, brain, StringComparison.Ordinal)))
        {
            if (_capabilitiesByMachineId.TryGetValue(machine.MachineId, out var cap)
                && await CheckHostedOsLiveAsync(machine, http.RequestAborted))
            {
                nodes.Add(cap);
            }
        }

        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            new BrainComputeNodesResponse(brain, nodes.ToArray()),
            DeviceWireJson.Default.BrainComputeNodesResponse,
            http.RequestAborted);
    }

    private async Task HandleCallerSandboxDispatchAsync(HttpContext http)
    {
        Volatile.Write(ref _lastSandboxDispatchDiag, "entered");
        var auth = await AuthenticateCallerServiceAssertionAsync(http);
        if (auth is null)
        {
            Volatile.Write(ref _lastSandboxDispatchDiag, "auth-failed");
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var dispatch = JsonSerializer.Deserialize(auth.Value.Body, DeviceWireJson.Default.BrainSandboxDispatchRequest);
        if (dispatch is null || !string.Equals(dispatch.Brain, auth.Value.Claims.Subject, StringComparison.Ordinal))
        {
            Volatile.Write(ref _lastSandboxDispatchDiag, "subject-mismatch");
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!_machinesById.TryGetValue(dispatch.MachineId, out var machine)
            || !string.Equals(machine.BrainId, dispatch.Brain, StringComparison.Ordinal)
            || !await CheckHostedOsLiveAsync(machine, http.RequestAborted))
        {
            Volatile.Write(ref _lastSandboxDispatchDiag, "machine-not-live");
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var pending = new PendingJob<ConnectedSandboxRequest, ConnectedSandboxResponse>(dispatch.Request);
        _sandboxJobs[dispatch.Request.JobId] = pending;
        _sandboxQueues.GetOrAdd(machine.MachineId, static _ => new ConcurrentQueue<PendingJob<ConnectedSandboxRequest, ConnectedSandboxResponse>>()).Enqueue(pending);
        Volatile.Write(ref _lastSandboxDispatchDiag, "enqueued");

        try
        {
            // Wait for the online worker to claim the job before flushing 200 OK headers;
            // if the worker is unresponsive, Hosted OS's <400ms availabilityTimeout aborts the request here.
            await pending.Claimed.Task.WaitAsync(http.RequestAborted);
            Volatile.Write(ref _lastSandboxDispatchDiag, "claimed");
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.StartAsync(http.RequestAborted);
            await http.Response.Body.WriteAsync(" "u8.ToArray(), http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
            Volatile.Write(ref _lastSandboxDispatchDiag, "headers-flushed");

            var result = await pending.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), http.RequestAborted);
            Volatile.Write(ref _lastSandboxDispatchDiag, "result-ready");
            await JsonSerializer.SerializeAsync(http.Response.Body, result, DeviceWireJson.Default.ConnectedSandboxResponse, http.RequestAborted);
            Volatile.Write(ref _lastSandboxDispatchDiag, "completed");
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _lastSandboxDispatchDiag, Volatile.Read(ref _lastSandboxDispatchDiag) + ":cancelled");
            _sandboxJobs.TryRemove(dispatch.Request.JobId, out _);
        }
    }

    private async Task HandleCallerResourceDispatchAsync(HttpContext http)
    {
        var auth = await AuthenticateCallerServiceAssertionAsync(http);
        if (auth is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var dispatch = JsonSerializer.Deserialize(auth.Value.Body, DeviceWireJson.Default.BrainResourceDispatchRequest);
        if (dispatch is null || !string.Equals(dispatch.Brain, auth.Value.Claims.Subject, StringComparison.Ordinal))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!_machinesById.TryGetValue(dispatch.MachineId, out var machine)
            || !string.Equals(machine.BrainId, dispatch.Brain, StringComparison.Ordinal)
            || !await CheckHostedOsLiveAsync(machine, http.RequestAborted))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var pending = new PendingJob<PcResourceRequest, PcResourceResponse>(dispatch.Request);
        _resourceJobs[dispatch.Request.JobId] = pending;
        _resourceQueues.GetOrAdd(machine.MachineId, static _ => new ConcurrentQueue<PendingJob<PcResourceRequest, PcResourceResponse>>()).Enqueue(pending);

        try
        {
            await pending.Claimed.Task.WaitAsync(http.RequestAborted);
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.StartAsync(http.RequestAborted);
            await http.Response.Body.WriteAsync(" "u8.ToArray(), http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);

            var result = await pending.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), http.RequestAborted);
            await JsonSerializer.SerializeAsync(http.Response.Body, result, DeviceWireJson.Default.PcResourceResponse, http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            _resourceJobs.TryRemove(dispatch.Request.JobId, out _);
        }
    }

    private async Task<EnrolledMachine?> AuthenticateWorkerAsync(HttpContext http, bool checkLive)
    {
        var authHeader = http.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var accessToken = authHeader["Bearer ".Length..].Trim();
        if (!_machinesByAccessToken.TryGetValue(accessToken, out var machine))
        {
            return null;
        }

        var proof = http.Request.Headers[CircleRoutes.MachineProofHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(proof))
        {
            return null;
        }

        var rawTarget = http.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw
            : http.Request.Path.ToUriComponent() + http.Request.QueryString.ToUriComponent();
        var requestUri = new Uri(Origin + rawTarget, UriKind.Absolute);

        var verified = MachineProof.Verify(
            machine.Spki,
            proof,
            http.Request.Method,
            requestUri,
            accessToken,
            DateTimeOffset.UtcNow,
            _machineProofNonces);
        if (!verified.IsValid)
        {
            return null;
        }

        if (checkLive && !await CheckHostedOsLiveAsync(machine, http.RequestAborted))
        {
            return null;
        }

        return machine;
    }

    private async Task<(ServiceAssertionClaims Claims, byte[] Body)?> AuthenticateCallerServiceAssertionAsync(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith(ServiceAssertion.Scheme + " ", StringComparison.Ordinal))
        {
            return null;
        }

        var token = authHeader[(ServiceAssertion.Scheme.Length + 1)..].Trim();
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        var body = ms.ToArray();

        var claims = ServiceAssertion.Verify(
            token,
            issuer => string.Equals(issuer, _hostedOsIssuer, StringComparison.Ordinal)
                ? (_hostedOsPublicKey, Origin)
                : null,
            TimeProvider.System);
        if (claims is null)
        {
            return null;
        }

        var rawTarget = http.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw
            : http.Request.Path.ToUriComponent() + http.Request.QueryString.ToUriComponent();
        var expectedReq = ServiceAssertion.Request(http.Request.Method, rawTarget, body);

        if (!string.Equals(claims.Operation, "circle-compute-dispatch", StringComparison.Ordinal)
            || !string.Equals(claims.Purpose, "circle-compute-dispatch", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(claims.Subject)
            || !string.Equals(claims.Request, expectedReq, StringComparison.Ordinal))
        {
            return null;
        }

        if (!_usedServiceJtis.TryAdd(claims.Jti, 0))
        {
            return null;
        }

        Interlocked.Increment(ref _serviceAssertionCallsVerified);
        return (claims, body);
    }

    private async Task<bool> CheckHostedOsLiveAsync(EnrolledMachine machine, CancellationToken ct)
    {
        var target = EnrollmentEdge.LiveFor(machine.BrainId, machine.MachineId, machine.Thumbprint);
        using var res = await SendToHostedOsAssertedAsync(EnrollmentEdge.LiveOperation, HttpMethod.Get, target, [], ct);
        if (!res.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        var live = JsonSerializer.Deserialize(json, EnrollmentEdgeJson.Default.LiveResponse);
        return live?.Live == true;
    }

    private async Task<HttpResponseMessage> SendToHostedOsAssertedAsync(
        string operation,
        HttpMethod method,
        string target,
        byte[] body,
        CancellationToken ct)
    {
        var claims = new ServiceAssertionClaims(
            Issuer: _relayIssuer,
            Audience: _hostedOsAudience,
            Operation: operation,
            Request: ServiceAssertion.Request(method.Method, target, body),
            Jti: Guid.NewGuid().ToString("N"),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30));
        var jwt = ServiceAssertion.Sign(claims, _relayKey, TimeProvider.System);

        using var req = new HttpRequestMessage(method, _hostedOsOrigin + target);
        if (body.Length > 0)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        req.Headers.Authorization = new AuthenticationHeaderValue(ServiceAssertion.Scheme, jwt);
        return await _hostedClient.SendAsync(req, ct);
    }

    private sealed record EnrolledMachine(
        string MachineId,
        string BrainId,
        string DeviceName,
        byte[] Spki,
        string Thumbprint,
        string AccessToken,
        string RefreshToken);

    private sealed class PendingJob<TRequest, TResponse>(TRequest request)
    {
        public TRequest Request { get; } = request;
        public TaskCompletionSource<bool> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TResponse> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InMemoryNonceCache : INonceCache
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

        public bool TryAdd(string jti, DateTimeOffset expires) => _seen.TryAdd(jti, expires);
    }
}
