using System.Net;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Modules.Microsoft.Windows;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

public sealed class WindowsEndpointFacts(WindowsMachineHost shared)
{
    [Trait("host", "own")]
    [Fact]
    public async Task FolderSetupSelectsWindowsWhenOtherConnectionTypesAreComposed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("windows-composed").FullName;
        await using var brain = await TestBrain.Create().WithModule<WindowsModule>().WithModule<OtherStorageModule>()
            .WithOptions<WindowsModule, WindowsOptions>(options => options.MachineIdFile = Path.Combine(folder, "machine-id"))
            .StartAsync(ct);
        var device = await brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/windows/device", ct);
        var id = device.GetProperty("machineId").GetString();
        using var connected = await brain.HttpClient.PostAsJsonAsync($"/brains/owner/connections/{id}/connect",
            new { connectorId = "windows", authMethodId = "device", value = id }, ct);
        connected.EnsureSuccessStatusCode();
        using var roots = await brain.HttpClient.GetAsync($"/brains/owner/windows/{id}/roots", ct);
        Assert.Equal(HttpStatusCode.OK, roots.StatusCode);
    }

    [Fact]
    public async Task DiscoverConnectAndChooseOnlyAnExplicitFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var http = host.Owner.Http;
        var device = await http.GetFromJsonAsync<JsonElement>($"/brains/{host.Owner.BrainId}/windows/device", ct);
        Assert.Equal(host.MachineId, device.GetProperty("machineId").GetString());
        Assert.Equal(Environment.MachineName, device.GetProperty("machineName").GetString());
        using var connected = await http.PostAsJsonAsync($"/brains/{host.Owner.BrainId}/connections/{host.MachineId}/connect",
            new { connectorId = "windows", authMethodId = "device", value = host.MachineId }, ct);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        var roots = $"/brains/{host.Owner.BrainId}/windows/{host.MachineId}/roots";
        Assert.Empty((await http.GetFromJsonAsync<string[]>(roots, ct))!);
        using var allowed = await http.PostAsJsonAsync($"{roots}/allow", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal([Path.GetFullPath(host.Root)], (await http.GetFromJsonAsync<string[]>(roots, ct))!);
        using var revoked = await http.PostAsJsonAsync($"{roots}/revoke", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<string[]>(roots, ct))!);
    }

    [Fact]
    public async Task FolderSetupRejectsDisconnectedMissingRelativeAndOtherMachinePaths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var http = host.Owner.Http;
        var roots = $"/brains/{host.Owner.BrainId}/windows/{host.MachineId}/roots";
        using var disconnected = await http.PostAsJsonAsync($"{roots}/allow", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.Conflict, disconnected.StatusCode);
        using var connected = await http.PostAsJsonAsync($"/brains/{host.Owner.BrainId}/connections/{host.MachineId}/connect",
            new { connectorId = "windows", authMethodId = "device", value = host.MachineId }, ct);
        connected.EnsureSuccessStatusCode();
        foreach (var path in new[] { "", "relative", Path.Combine(host.Root, "missing") })
        {
            using var invalid = await http.PostAsJsonAsync($"{roots}/allow", new { path }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var other = await http.GetAsync($"/brains/{host.Owner.BrainId}/windows/{Guid.NewGuid():N}/roots", ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<string[]>(roots, ct))!);
    }

    [Fact]
    public async Task DiscoveryAndFoldersEnforceBrainMembership()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var alice = await shared.PersonAsync("alice", ct);
        var bob = await shared.PersonAsync("bob", ct);
        using var discovery = await alice.Http.GetAsync($"/brains/{bob.BrainId}/windows/device", ct);
        Assert.Equal(HttpStatusCode.Forbidden, discovery.StatusCode);
        using var write = await alice.Http.PostAsJsonAsync($"/brains/{bob.BrainId}/windows/{host.MachineId}/roots/allow", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}

public sealed class OtherStorageModule() : Module(new ModuleId("other-storage"))
{
    public static ConnectorDefinition Connector => new("other-storage", "Other storage", ["api-key"], []) { Neurons = [typeof(IOtherStorage)] };
    public override Type? Neurons => typeof(OtherStorageNeurons);
    public override void Configure(ISiloBuilder silo) { }
}

[DigitalBrain.Contracts.NeuronAlias("other-storage"), DigitalBrain.Contracts.DefaultNeuronType("other-storage")]
public interface IOtherStorage { }

[DigitalBrain.Neuron("other-storage", State = "other-storage")]
internal sealed class OtherStorageNeuron()
    : ConnectionNeuron<ConnectionState>, IOtherStorage
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(ConnectionProbeResult.Connected());
}

// The fact's connection neurons are plain (06-08): this module's assembly [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module("other-storage")]
public sealed class OtherStorageNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
