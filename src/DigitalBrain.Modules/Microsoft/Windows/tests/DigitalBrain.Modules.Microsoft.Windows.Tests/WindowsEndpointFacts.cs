using System.Net;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

public sealed class WindowsEndpointFacts
{
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
        var host = await WindowsHost.StartAsync(false, false, ct);
        await using var brain = host.Brain;
        var http = brain.HttpClient;
        var device = await http.GetFromJsonAsync<JsonElement>("/brains/owner/windows/device", ct);
        Assert.Equal(host.MachineId, device.GetProperty("machineId").GetString());
        Assert.Equal(Environment.MachineName, device.GetProperty("machineName").GetString());
        using var connected = await http.PostAsJsonAsync($"/brains/owner/connections/{host.MachineId}/connect",
            new { connectorId = "windows", authMethodId = "device", value = host.MachineId }, ct);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        var roots = $"/brains/owner/windows/{host.MachineId}/roots";
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
        var host = await WindowsHost.StartAsync(false, false, ct);
        await using var brain = host.Brain;
        var http = brain.HttpClient;
        var roots = $"/brains/owner/windows/{host.MachineId}/roots";
        using var disconnected = await http.PostAsJsonAsync($"{roots}/allow", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.Conflict, disconnected.StatusCode);
        using var connected = await http.PostAsJsonAsync($"/brains/owner/connections/{host.MachineId}/connect",
            new { connectorId = "windows", authMethodId = "device", value = host.MachineId }, ct);
        connected.EnsureSuccessStatusCode();
        foreach (var path in new[] { "", "relative", Path.Combine(host.Root, "missing") })
        {
            using var invalid = await http.PostAsJsonAsync($"{roots}/allow", new { path }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var other = await http.GetAsync($"/brains/owner/windows/{Guid.NewGuid():N}/roots", ct);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<string[]>(roots, ct))!);
    }

    [Fact]
    public async Task DiscoveryAndFoldersEnforceBrainMembership()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.StartAsync(false, false, ct);
        await using var brain = host.Brain;
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        using var discovery = await alice.Http.GetAsync($"/brains/{bob.BrainId}/windows/device", ct);
        Assert.Equal(HttpStatusCode.Forbidden, discovery.StatusCode);
        using var write = await alice.Http.PostAsJsonAsync($"/brains/{bob.BrainId}/windows/{host.MachineId}/roots/allow", new { path = host.Root }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}

public sealed class OtherStorageModule() : Module(new ModuleId("other-storage"))
{
    public static ConnectorDefinition Connector => new("other-storage", "Other storage", ["api-key"], []) { Neurons = [typeof(IOtherStorage)] };
    public override void Configure(ISiloBuilder silo) { }
}

[Alias("other-storage"), Orleans.Metadata.DefaultGrainType("other-storage")]
public interface IOtherStorage : INeuron { }

[GrainType("other-storage")]
internal sealed class OtherStorageNeuron(
    [PersistentState("other-storage", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<ConnectionState> store)
    : ConnectionNeuron<ConnectionState>(store), IOtherStorage
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(ConnectionProbeResult.Connected());
}
