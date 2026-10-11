using System.Globalization;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Mesh;

// This machine as a volunteer node (T23-1, T23-5): once the machine is bound to its account it registers node/{machine}
// for that account, signed by its own key, offering OfferedBytes; then it beats every HeartbeatEvery with the bytes it
// holds, so the directory places by room and knows it is online. A failed registration is retried on the next beat.
internal sealed partial class StorageNodeService(ShardStore store, INodeDirectoryClient directory, TrustSigner signer, MachineIdentity machine,
    IOptions<StorageNodeOptions> options, TimeProvider time, ILogger<StorageNodeService> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _beating = Task.CompletedTask;

    public string NodeId => StorageNodes.IdOf(machine.MachineId);

    public bool Registered { get; private set; }

    public async Task Start()
    {
        try { await Register(); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.NotRegistered(logger, error, NodeId); }
        _beating = Beat();
    }

    // Owner = the account this machine serves (its first binding, R55); re-registration only changes the offer.
    public async Task Register()
    {
        var account = (await directory.Machine(machine.MachineId))?.Accounts.FirstOrDefault()
            ?? throw new InvalidOperationException($"Machine {machine.MachineId} serves no account yet; its node is registered once it does.");
        var offered = options.Value.OfferedBytes;
        await signer.Signed(new NodeRegistration(NodeId, machine.MachineId, account, offered), account, directory.RegisterNode);
        Registered = true;
        Log.RegisteredNode(logger, NodeId, account, offered.ToString(CultureInfo.InvariantCulture));
    }

    public async Task Heartbeat()
    {
        if (!Registered) { await Register(); return; }
        await directory.Heartbeat(NodeId, store.UsedBytes);
        Log.Beat(logger, NodeId, store.UsedBytes);
    }

    private async Task Beat()
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatEvery, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try { await Heartbeat(); }
                catch (Exception error) when (error is not OperationCanceledException) { Log.NotBeaten(logger, error, NodeId); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) { return; }
        await _stop.CancelAsync();
        await _beating;
        _stop.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Storage node {NodeId} registered for account {Account}, offering {OfferedBytes} bytes.")]
        public static partial void RegisteredNode(ILogger logger, string nodeId, string account, string offeredBytes);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Storage node {NodeId} beat with {UsedBytes} bytes used.")]
        public static partial void Beat(ILogger logger, string nodeId, long usedBytes);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage node {NodeId} could not register; it tries again on its next heartbeat.")]
        public static partial void NotRegistered(ILogger logger, Exception error, string nodeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage node {NodeId} missed a heartbeat.")]
        public static partial void NotBeaten(ILogger logger, Exception error, string nodeId);
    }
}

public static class StorageNodeHosting
{
    // T23-1: a machine of a separate trust cluster offering storage (StorageNode:OfferedBytes > 0) answers node.shard.* on
    // its machine links and keeps itself registered. Without an offer, or without a trust link, nothing is added.
    public static ISiloBuilder AddStorageNodeWhenOffered(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var offered = silo.Configuration[$"{StorageNodeOptions.Key}:{nameof(StorageNodeOptions.OfferedBytes)}"];
        if (string.IsNullOrWhiteSpace(offered) || !long.TryParse(offered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0) { return silo; }
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { Node(silo); }
        return silo;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void Node(ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(StorageNodeService))) { return; }
        services.AddOptions<StorageNodeOptions>().BindConfiguration(StorageNodeOptions.Key)
            .PostConfigure<IOptions<Parts.PartsOptions>>((node, parts) => node.Directory ??= Path.Combine(parts.Value.MachineDirectory, "node"))
            .Validate(node => { node.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton(provider => new ShardStore(provider.GetRequiredService<IOptions<StorageNodeOptions>>().Value,
            provider.GetRequiredService<IOptions<MeshOptions>>().Value.MaxShardBytes));
        services.TryAddSingleton<StorageNodeEndpoint>();
        services.TryAddSingleton<StorageNodeService>();
        silo.AddStartupTask((provider, _) =>
        {
            var endpoint = provider.GetRequiredService<StorageNodeEndpoint>();
            var transport = provider.GetRequiredService<Links.QuicMachineTransport>();
            foreach (var operation in NodeOperations.All) { transport.Handle(operation, endpoint.Answer); }
            return Task.CompletedTask;
        }, ServiceLifecycleStage.RuntimeGrainServices);
        // After MachineRegistration (Active - 1) bound this machine to its account.
        silo.AddStartupTask((provider, _) => provider.GetRequiredService<StorageNodeService>().Start(), ServiceLifecycleStage.Active);
    }
}
