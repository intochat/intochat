using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Google.Gmail;
using IntoChat.OS.Sandbox;
using IntoChat.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Tests.Unit.Sandbox;

public interface ISampleSlotContract
{
    Task<string> EchoAsync(string text, CancellationToken cancellationToken = default);
    Task PingAsync(CancellationToken cancellationToken = default);
}

[Signal("sample.signal")]
public sealed record SampleSandboxSignal(string Status) : Signal;

public sealed class SampleSandboxState
{
    public int Count { get; set; }
    public string LastSignal { get; set; } = string.Empty;
    public int ScheduleRuns { get; set; }
}

public sealed class SampleSandboxSettings
{
    public string Prefix { get; set; } = "Count";
}

[App("sample-sandbox-app", Title = "Sample Sandbox App", Summary = "Test app for AppSandboxHost")]
public sealed class SampleSandboxApp : BrainApp<SampleSandboxState, SampleSandboxSettings>
{
    [OnCommand("increment", Description = "Increments the counter")]
    public int Increment(int amount = 1)
    {
        State.Count += amount;
        return State.Count;
    }

    [OnCommand("fail", Description = "Throws a test exception")]
    public void Fail(string reason) => throw new InvalidOperationException(reason);

    [OnSignal("sample.ping")]
    public async Task OnPingAsync(SampleSandboxSignal signal, CancellationToken cancellationToken)
    {
        State.LastSignal = signal.Status;
        await Brain.Notifications.NotifyAsync("Ping", signal.Status, NotificationPriority.Low, cancellationToken)
            .ConfigureAwait(false);
    }

    [Every("@hourly")]
    public Task OnHourlyAsync(CancellationToken cancellationToken)
    {
        State.ScheduleRuns++;
        return Brain.LogAsync("hourly-tick", cancellationToken);
    }

    public override View Render()
        => new("Sample", new Text($"{Settings.Prefix}: {State.Count} ({State.LastSignal})"));
}

public sealed class IntoChatClientFacts
{
    private static readonly Uri KernelUri = new("http://kernel.test:8080", UriKind.Absolute);

    // The kernel hands the sandbox its own bin folder, which holds every module. Sandboxed code may load a module's Contracts
    // assembly, never its implementation: an implementation brings its process, file, network and Orleans code with it.
    // This test project references every module, so its bin folder is the kernel's set.
    [Fact]
    public void AppLoadContextAdmitsModuleContractsButNoModuleImplementation()
    {
        var modules = Directory.EnumerateFiles(AppContext.BaseDirectory, "IntoChat.OS.Modules.*.dll")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("IntoChat.OS.Modules.Infrastructure", modules);
        Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Contracts"));

        var wrong = modules.Where(name => AppLoadContext.IsAssemblyAllowed(name) != ShouldAdmit(name))
            .Select(name => $"{name}: {(AppLoadContext.IsAssemblyAllowed(name) ? "admitted" : "refused")}")
            .ToArray();
        Assert.True(wrong.Length == 0, string.Join(Environment.NewLine, wrong));

        static bool ShouldAdmit(string name)
            => name.EndsWith(".Contracts", StringComparison.Ordinal) && !name.StartsWith("IntoChat.OS.Modules.Identity", StringComparison.Ordinal);
    }

    // Component assemblies (IntoChat.<Name>) are refused, as the Aspire contracts were when they sat
    // inside IntoChat.OS.Modules.Infrastructure. Admitting a component's contracts is an owner decision per component (OWED.md).
    [Fact]
    public void AppLoadContextRefusesComponentAssemblies()
    {
        var components = Directory.EnumerateFiles(AppContext.BaseDirectory, "IntoChat.Aspire*.dll")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["IntoChat.Aspire"], components);

        foreach (var name in components)
        {
            Assert.False(AppLoadContext.IsAssemblyAllowed(name), $"{name} is admitted to the sandbox");
        }
        Assert.False(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Modules.Infrastructure"));
    }

    // The activation hook hands a caller the live neuron of any app activated in its async flow; sandboxed code must not reach it.
    [Fact]
    public void AppLoadContextRefusesTheAssemblyHoldingTheActivationHook()
        => Assert.False(AppLoadContext.IsAssemblyAllowed(typeof(IntoChat.OS.Host.Apps.AppActivationScope).Assembly.GetName().Name));

    [Fact]
    public void AppLoadContextAllowsSdkAndRefusesForbiddenHostAssemblies()
    {
        var alc = new AppLoadContext("test-sandbox-alc");
        try
        {
            var osAssembly = alc.LoadFromAssemblyName(new AssemblyName("IntoChat.OS"));
            Assert.Same(typeof(ISandboxBrain).Assembly, osAssembly);

            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Contracts"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.UI"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Modules.AI.Contracts"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Modules.Catalog.Contracts"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("IntoChat.OS.Modules.Shell.Contracts"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("DigitalBrain"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("DigitalBrain.UI"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("Microsoft.Extensions.Logging.Abstractions"));
            Assert.True(AppLoadContext.IsAssemblyAllowed("System.Runtime"));

            string[] forbidden =
            [
                "System.Diagnostics.Process",
                "System.Net.Http",
                "Microsoft.AspNetCore.Http",
                "IntoChat.OS.Kernel",
                "IntoChat.OS.Modules.Identity",
                "IntoChat.OS.Modules.Identity.Contracts",
                "IntoChat.OS.Modules.Infrastructure",
                "IntoChat.OS.Modules.Plugins",
                "IntoChat.OS.Modules.Roslyn",
                "IntoChat.OS.Modules.AI",
                "IntoChat.OS.Modules.Catalog",
                "IntoChat.OS.Modules.Workspace",
                "IntoChat.OS.Modules.Contracts",
                "IntoChat.OS.Modules.Infrastructure.Contracts.Extra",
                "IntoChat.OS.AppHost",
                "DigitalBrain.Runtime.Orleans",
                "DigitalBrain.Identity",
            ];

            foreach (var name in forbidden)
            {
                Assert.False(AppLoadContext.IsAssemblyAllowed(name));
                Assert.Throws<FileLoadException>(() => alc.LoadFromAssemblyName(new AssemblyName(name)));
            }
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public async Task IntoChatClientForwardsNotificationsFolderTablesSlotAndSignalsOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        var createdUtc = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path == "/edge/sandbox/invoke")
            {
                var invoke = JsonSerializer.Deserialize<SandboxInvokeRequest>(request.Body, JsonSerializerOptions.Web)!;
                if (invoke.Contract == typeof(INotifications).FullName && invoke.Method == nameof(INotifications.NotifyAsync))
                {
                    var receipt = new NotificationReceipt("notif-1", false, createdUtc);
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web)));
                }

                if (invoke.Contract == typeof(IFolder).FullName && invoke.Method == nameof(IFolder.ListAsync))
                {
                    FolderEntry[] entries = [new("report.txt", "sub/report.txt", false, 42, createdUtc)];
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(entries, JsonSerializerOptions.Web)));
                }

                if (invoke.Contract == typeof(IFolder).FullName && invoke.Method == nameof(IFolder.WriteBytesAsync))
                {
                    return JsonResponse(new SandboxInvokeResponse(null));
                }

                if (invoke.Contract == typeof(IFolder).FullName && invoke.Method == nameof(IFolder.ReadBytesAsync))
                {
                    byte[] bytes = [1, 2, 3, 4];
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(bytes, JsonSerializerOptions.Web)));
                }

                if (invoke.Contract == typeof(ITableHandle).FullName && invoke.Method == nameof(ITableHandle.UpsertAsync))
                {
                    return JsonResponse(new SandboxInvokeResponse(null));
                }

                if (invoke.Contract == typeof(IAi).FullName && invoke.Method == nameof(IAi.AskAsync))
                {
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize("ai-answer", JsonSerializerOptions.Web)));
                }

                if (invoke.Contract == typeof(ISampleSlotContract).FullName && invoke.Method == nameof(ISampleSlotContract.EchoAsync))
                {
                    var args = JsonSerializer.Deserialize<string[]>(invoke.ArgsJson, JsonSerializerOptions.Web)!;
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize("echo:" + args[0], JsonSerializerOptions.Web)));
                }

                if (invoke.Contract == typeof(ISampleSlotContract).FullName && invoke.Method == nameof(ISampleSlotContract.PingAsync))
                {
                    return JsonResponse(new SandboxInvokeResponse(null));
                }
            }

            if (path is "/edge/sandbox/signals" or "/edge/sandbox/logs")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        var client = new IntoChatClient("brain-1", "sample-app", KernelUri, "secret-token", httpClient, NullLogger.Instance);

        var receipt = await client.Notifications.NotifyAsync("Alert", "Disk full", NotificationPriority.High, ct);
        Assert.Equal("notif-1", receipt.Id);

        var entries = await client.Folder("downloads").ListAsync("sub", ct);
        var entry = Assert.Single(entries);
        Assert.Equal("report.txt", entry.Name);

        byte[] written = [1, 2, 3, 4];
        await client.Folder("downloads").WriteBytesAsync("bin/data.bin", written, ct);
        var readBytes = await client.Folder("downloads").ReadBytesAsync("bin/data.bin", ct);
        Assert.Equal(written, readBytes);

        await client.Tables.Table("t").UpsertAsync("k1", "{\"v\":1}", ct);
        var aiAnswer = await client.Ai.AskAsync("Summarize", null, ct);
        Assert.Equal("ai-answer", aiAnswer);

        var slotProxy = client.Slot<ISampleSlotContract>("custom-slot");
        var echoed = await slotProxy.EchoAsync("hello", ct);
        Assert.Equal("echo:hello", echoed);
        await slotProxy.PingAsync(ct);

        await client.PublishAsync(new SampleSandboxSignal("ok"), ct);
        await client.LogAsync("client-check", ct);

        Assert.All(handler.Requests, req =>
        {
            Assert.Equal("secret-token", req.AppToken);
            Assert.Equal("brain-1", req.BrainId);
            Assert.Equal("sample-app", req.AppKey);
        });

        var slotEchoCall = handler.Requests.Single(r =>
            r.Uri.AbsolutePath == "/edge/sandbox/invoke"
            && JsonSerializer.Deserialize<SandboxInvokeRequest>(r.Body, JsonSerializerOptions.Web) is { Contract: var c, Method: "EchoAsync" }
            && c == typeof(ISampleSlotContract).FullName);
        var slotEchoPayload = JsonSerializer.Deserialize<SandboxInvokeRequest>(slotEchoCall.Body, JsonSerializerOptions.Web)!;
        Assert.Equal("custom-slot", slotEchoPayload.Slot);
        Assert.Equal("[\"hello\"]", slotEchoPayload.ArgsJson);

        var bytesWriteCall = handler.Requests.Single(r =>
            r.Uri.AbsolutePath == "/edge/sandbox/invoke"
            && JsonSerializer.Deserialize<SandboxInvokeRequest>(r.Body, JsonSerializerOptions.Web) is { Method: "WriteBytesAsync" });
        var bytesWritePayload = JsonSerializer.Deserialize<SandboxInvokeRequest>(bytesWriteCall.Body, JsonSerializerOptions.Web)!;
        Assert.Contains(Convert.ToBase64String(written), bytesWritePayload.ArgsJson, StringComparison.Ordinal);

        var signalCall = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath == "/edge/sandbox/signals");
        var signalPayload = JsonSerializer.Deserialize<SandboxSignalRequest>(signalCall.Body, JsonSerializerOptions.Web)!;
        Assert.Equal("sample.signal", signalPayload.SignalName);
        Assert.Contains("ok", signalPayload.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlotReturnsNeuronProxyAndMapsForbiddenToUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(typeof(IntoChatClient).Assembly.GetType("IntoChat.OS.Sandbox.ContractDispatchProxy"));

        var denyReads = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Uri.AbsolutePath == "/edge/sandbox/invoke")
            {
                if (denyReads)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new SandboxInvokeResponse(null, "Missing grant for gmail."), JsonSerializerOptions.Web),
                            Encoding.UTF8,
                            "application/json"),
                    };
                }

                var result = GmailInboxResult.FromFailure(new GmailFailure("not_connected", "Connect Gmail.", []));
                return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(result, JsonSerializerOptions.Web)));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var client = new IntoChatClient("brain-1", "triage-app", KernelUri, "secret-token", httpClient, NullLogger.Instance);

        var gmail = client.Slot<IGmail>("connection/gmail/inbox");
        Assert.IsAssignableFrom<DispatchProxy>(gmail);

        var ok = await gmail.ReadInbox(new GmailInboxRequest(5), ct);
        Assert.NotNull(ok.Failure);
        Assert.Equal("not_connected", ok.Failure!.Code);

        var invoke = JsonSerializer.Deserialize<SandboxInvokeRequest>(Assert.Single(handler.Requests).Body, JsonSerializerOptions.Web)!;
        Assert.Equal("gmail", invoke.Contract);
        Assert.Equal("connection/gmail/inbox", invoke.Slot);
        Assert.Equal(nameof(IGmail.ReadInbox), invoke.Method);

        denyReads = true;
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gmail.ReadInbox(new GmailInboxRequest(5), ct));
        Assert.Contains("Missing grant", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppSandboxHostLoadsExecutesAndRendersRuntimeSynapseUnit()
    {
        var ct = TestContext.Current.CancellationToken;
        var notified = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Uri.AbsolutePath == "/edge/sandbox/invoke")
            {
                var invoke = JsonSerializer.Deserialize<SandboxInvokeRequest>(request.Body, JsonSerializerOptions.Web)!;
                if (invoke.Contract == "notifications.slot" && invoke.Slot == "inbox" && invoke.Method == "Notify")
                {
                    notified.Add(invoke.ArgsJson);
                    var receipt = JsonDocument.Parse("{\"id\":\"notif-syn-1\",\"deduplicated\":false}").RootElement;
                    return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web)));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var host = new AppSandboxHost(httpClient, KernelUri, NullLogger<AppSandboxHost>.Instance);

        const string unitCode = """
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain;
            using DigitalBrain.UI;
            using INotifySlot = IntoChat.OS.Modules.Apps.Notifications.INotifications;

            namespace SampleSynapse;

            [Signal("triage.requested")]
            public sealed record TriageRequested(string Tag) : Signal;

            [State("inbox.triage")]
            public sealed record TriageState(int Count = 0, string LatestReceipt = "")
            {
                public TriageState() : this(0, "") { }
            }

            public static class TriageSynapse
            {
                [Synapse(typeof(TriageRequested), "inbox.triage")]
                public static async Task<Reaction<TriageState>> OnRequested(
                    TriageRequested sig,
                    TriageState state,
                    [DigitalBrain.Slot(Name = "inbox")] INotifySlot notifications,
                    CancellationToken ct)
                {
                    var receipt = await notifications.Notify("Triaged", sig.Tag, "normal", ct);
                    var id = receipt.GetProperty("id").GetString() ?? "none";
                    return Reaction<TriageState>.Of(state with { Count = state.Count + 1, LatestReceipt = $"{sig.Tag}:{id}" });
                }

                [Synapse("inbox.triage")]
                public static View Render(TriageState state)
                    => new("Inbox Triage", new Text($"{state.Count}:{state.LatestReceipt}"));
            }
            """;

        var compiler = new SynapseCompiler(ReferenceSet.FromDefaultContext());
        var build = compiler.Compile(new SynapseSource("inbox-triage", new Dictionary<string, string> { ["Triage.cs"] = unitCode }, null));
        Assert.True(build.Success, string.Join("\n", build.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}")));

        try
        {
            var deploy = await host.DeployAsync(
                new DeployAppRequest(
                    BrainId: "brain-99",
                    AppKey: "inbox.triage",
                    Version: "1.0.0",
                    Digest: build.Hash,
                    AssemblyBase64: Convert.ToBase64String(build.Assembly),
                    KernelUrl: KernelUri.ToString(),
                    AppToken: "tok-99"),
                ct);

            Assert.Contains("triage.requested", deploy.Signals);
            Assert.NotNull(deploy.InitialViewJson);

            var dispatch = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-99",
                    AppKey: "inbox.triage",
                    Kind: "signal",
                    Name: "triage.requested",
                    PayloadJson: "{\"tag\":\"vip\"}",
                    StateJson: deploy.StateJson),
                ct);

            Assert.True(dispatch.Handled, dispatch.Error);
            Assert.Null(dispatch.Error);
            Assert.Single(notified);
            Assert.Contains("1:vip:notif-syn-1", dispatch.UpdatedViewJson!, StringComparison.Ordinal);

            var render = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-99",
                    AppKey: "inbox.triage",
                    Kind: "render",
                    StateJson: dispatch.UpdatedStateJson),
                ct);

            Assert.True(render.Handled, render.Error);
            Assert.Contains("1:vip:notif-syn-1", render.UpdatedViewJson!, StringComparison.Ordinal);
        }
        finally
        {
            if (host.LoadedAppCount > 0)
            {
                await host.UnloadAsync("brain-99", "inbox.triage", ct);
            }
        }
    }

    [Fact]
    public async Task AppSandboxHostDeploysDispatchesAndUnloadsApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Uri.AbsolutePath == "/edge/sandbox/invoke")
            {
                var receipt = new NotificationReceipt("notif-2", false, DateTimeOffset.UtcNow);
                return JsonResponse(new SandboxInvokeResponse(JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web)));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var host = new AppSandboxHost(httpClient, KernelUri, NullLogger<AppSandboxHost>.Instance);

        const string sampleSource = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using DigitalBrain;
            using DigitalBrain.UI;
            using IntoChat.Sdk;

            namespace SampleSandbox;

            [Signal("sample.signal")]
            public sealed record SampleSandboxSignal(string Status) : Signal;

            public sealed class SampleSandboxState
            {
                public int Count { get; set; }
                public string LastSignal { get; set; } = string.Empty;
                public int ScheduleRuns { get; set; }
            }

            public sealed class SampleSandboxSettings
            {
                public string Prefix { get; set; } = "Count";
            }

            [App("sample-sandbox-app", Title = "Sample Sandbox App", Summary = "Test app for AppSandboxHost")]
            public sealed class SampleSandboxApp : BrainApp<SampleSandboxState, SampleSandboxSettings>
            {
                [OnCommand("increment", Description = "Increments the counter")]
                public int Increment(int amount = 1)
                {
                    State.Count += amount;
                    return State.Count;
                }

                [OnCommand("fail", Description = "Throws a test exception")]
                public void Fail(string reason) => throw new InvalidOperationException(reason);

                [OnSignal("sample.ping")]
                public async Task OnPingAsync(SampleSandboxSignal signal, CancellationToken cancellationToken)
                {
                    State.LastSignal = signal.Status;
                    await Brain.Notifications.NotifyAsync("Ping", signal.Status, NotificationPriority.Low, cancellationToken)
                        .ConfigureAwait(false);
                }

                [Every("@hourly")]
                public Task OnHourlyAsync(CancellationToken cancellationToken)
                {
                    State.ScheduleRuns++;
                    return Brain.LogAsync("hourly-tick", cancellationToken);
                }

                public override View Render()
                    => new("Sample", new Text($"{Settings.Prefix}: {State.Count} ({State.LastSignal})"));
            }
            """;

        var package = new AppPackage(
            "sample-sandbox-app",
            "1.0.0",
            "Sample Sandbox App",
            "Test app for AppSandboxHost",
            "apps",
            "General",
            "test",
            [new AppSourceFile("SampleSandboxApp.cs", sampleSource)],
            new AppPackageNeeds([], [], [], []),
            "{}",
            "sha256:test");
        var compiled = new AppPackageCompiler().Compile(package);
        Assert.True(compiled.Succeeded, string.Join(Environment.NewLine, compiled.Errors));
        var assemblyBytes = compiled.AssemblyBytes;

        try
        {
            var deployResponse = await host.DeployAsync(
                new DeployAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Version: "1.0.0",
                    Digest: "sha256:test",
                    AssemblyBase64: Convert.ToBase64String(assemblyBytes),
                    StateJson: null,
                    SettingsJson: "{\"prefix\":\"Items\"}",
                    KernelUrl: KernelUri.ToString(),
                    AppToken: "app-token-42"),
                ct);

            Assert.Equal(1, host.LoadedAppCount);
            Assert.Equal("Sample Sandbox App", deployResponse.Title);
            Assert.Contains(deployResponse.Commands, c => c.Name == "increment");
            Assert.Contains("sample.ping", deployResponse.Signals);
            Assert.Contains("@hourly", deployResponse.Schedules);
            Assert.NotNull(deployResponse.InitialViewJson);
            Assert.Contains("Items: 0", deployResponse.InitialViewJson, StringComparison.Ordinal);

            var cmdResult = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "command",
                    Name: "increment",
                    PayloadJson: "{\"amount\":5}"),
                ct);

            Assert.True(cmdResult.Handled);
            Assert.Null(cmdResult.Error);
            Assert.Equal("5", cmdResult.ResultJson);
            Assert.Contains("\"count\":5", cmdResult.UpdatedStateJson!, StringComparison.Ordinal);
            Assert.Contains("Items: 5", cmdResult.UpdatedViewJson!, StringComparison.Ordinal);

            var sigResult = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "signal",
                    Name: "sample.ping",
                    PayloadJson: "{\"status\":\"healthy\"}"),
                ct);

            Assert.True(sigResult.Handled);
            Assert.Null(sigResult.Error);
            Assert.Contains("healthy", sigResult.UpdatedStateJson!, StringComparison.Ordinal);
            Assert.Contains("Items: 5 (healthy)", sigResult.UpdatedViewJson!, StringComparison.Ordinal);

            var scheduleResult = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "schedule",
                    Name: "@hourly"),
                ct);

            Assert.True(scheduleResult.Handled);
            Assert.Contains("\"scheduleRuns\":1", scheduleResult.UpdatedStateJson!, StringComparison.Ordinal);

            var unknownCmd = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "command",
                    Name: "does-not-exist"),
                ct);

            Assert.False(unknownCmd.Handled);

            var renderResult = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "render"),
                ct);

            Assert.True(renderResult.Handled);
            Assert.Contains("Items: 5 (healthy)", renderResult.UpdatedViewJson!, StringComparison.Ordinal);

            var failedCmd = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "command",
                    Name: "fail",
                    PayloadJson: "{\"reason\":\"boom\"}"),
                ct);

            Assert.True(failedCmd.Handled);
            Assert.Equal("boom", failedCmd.Error);
            Assert.Contains("\"count\":5", failedCmd.UpdatedStateJson!, StringComparison.Ordinal);

            var rehydratedRender = await host.DispatchAsync(
                new DispatchAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Kind: "render",
                    StateJson: "{\"count\":99,\"lastSignal\":\"rehydrated\",\"scheduleRuns\":2}"),
                ct);

            Assert.True(rehydratedRender.Handled);
            Assert.Contains("Items: 99 (rehydrated)", rehydratedRender.UpdatedViewJson!, StringComparison.Ordinal);

            var redeploy = await host.DeployAsync(
                new DeployAppRequest(
                    BrainId: "brain-42",
                    AppKey: "sample-sandbox-app",
                    Version: "1.0.1",
                    Digest: "sha256:test2",
                    AssemblyBase64: Convert.ToBase64String(assemblyBytes)),
                ct);

            Assert.Equal("1.0.1", redeploy.Version);
            Assert.Equal(1, host.LoadedAppCount);

            var unload = await host.UnloadAsync("brain-42", "sample-sandbox-app", ct);
            Assert.True(unload.Unloaded);
            Assert.Equal(0, host.LoadedAppCount);
        }
        finally
        {
            if (host.LoadedAppCount > 0)
            {
                await host.UnloadAsync("brain-42", "sample-sandbox-app", ct);
            }
        }
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };

    private sealed record CapturedHttpRequest(
        Uri Uri,
        string Body,
        string? AppToken,
        string? BrainId,
        string? AppKey);

    private sealed class StubHttpMessageHandler(Func<CapturedHttpRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedHttpRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var captured = new CapturedHttpRequest(
                request.RequestUri!,
                body,
                request.Headers.TryGetValues("X-App-Token", out var t) ? t.FirstOrDefault() : null,
                request.Headers.TryGetValues("X-Brain-Id", out var b) ? b.FirstOrDefault() : null,
                request.Headers.TryGetValues("X-App-Key", out var a) ? a.FirstOrDefault() : null);

            Requests.Add(captured);
            return responder(captured);
        }
    }
}
