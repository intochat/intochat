using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.Aspire;
using IntoChat.Aspire.Bridge;
using IntoChat.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class AspireUdsBridgeFacts
{
    private sealed class ProbeResource(string name) : Resource(name);

    [Fact]
    public async Task UdsBridgeStreamsSnapshotsExecutesCommandsAndDetectsDisconnect()
    {
        var os = AppHostChainFacts.Compose("local");
        var probe = new ProbeResource("bridge-probe");
        var invoked = 0;
        var rb = os.ApplicationBuilder.AddResource(probe)
            .WithCommand("ping", "Ping", _ =>
            {
                Interlocked.Increment(ref invoked);
                return Task.FromResult(CommandResults.Success());
            })
            .WithCommand("fail-me", "Fail Me", _ => Task.FromResult(CommandResults.Failure("boom")));

        await using var app = os.ApplicationBuilder.Build();
        var endpoint = app.Services.GetRequiredService<AspireBridgeEndpoint>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var loggers = app.Services.GetRequiredService<ResourceLoggerService>();

        var server = new AspireBridgeServer(endpoint, notifications, commands, loggers);
        var store = new AspireHostStore();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AspireBridgeProtocol.SocketPathKey] = endpoint.SocketPath })
            .Build();
        var client = new AspireBridgeClient(store, config, new ServiceCollection().BuildServiceProvider());

        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var clientCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await server.StartAsync(serverCts.Token);
        await client.StartAsync(clientCts.Token);

        // Publish a snapshot with visible, hidden, and argument-taking commands.
        await notifications.PublishUpdateAsync(probe, state => state with
        {
            ResourceType = "Probe",
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
            Commands =
            [
                new ResourceCommandSnapshot("ping", ResourceCommandState.Enabled, "Ping", null, null, null, null, null, false),
                new ResourceCommandSnapshot("hidden-cmd", ResourceCommandState.Hidden, "Hidden", null, null, null, null, null, false),
                new ResourceCommandSnapshot("arg-cmd", ResourceCommandState.Enabled, "WithArg", null, null, null, null, null, false)
                {
                    Arguments = [new InteractionInput { Name = "x", InputType = InputType.Text, Label = "X" }],
                },
            ],
        });

        // Wait for the snapshot to arrive over UDS.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !store.Read().Items.Any(i => i.Name == "bridge-probe"))
        {
            await Task.Delay(50, clientCts.Token);
        }

        Assert.True(store.Connected);
        var resource = Assert.Single(store.Read().Items, i => i.Name == "bridge-probe");
        var instance = Assert.Single(resource.Instances);
        Assert.Equal("Running", instance.State);
        Assert.Contains("ping", instance.Commands.Select(c => c.Name));
        Assert.DoesNotContain("hidden-cmd", instance.Commands.Select(c => c.Name));
        Assert.DoesNotContain("arg-cmd", instance.Commands.Select(c => c.Name));

        // Execute a real command through UDS.
        var ok = await client.ExecuteAsync(instance.Id, "ping", TimeSpan.FromSeconds(5), clientCts.Token);
        Assert.True(ok.Succeeded);
        Assert.Equal(1, invoked);

        var failed = await client.ExecuteAsync(instance.Id, "fail-me", TimeSpan.FromSeconds(5), clientCts.Token);
        Assert.False(failed.Succeeded);
        Assert.Contains("boom", failed.Message ?? "", StringComparison.Ordinal);

        // Stop the server -> client detects EOF disconnect immediately.
        await server.StopAsync(CancellationToken.None);
        var disconnectDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < disconnectDeadline && store.Connected)
        {
            await Task.Delay(50, CancellationToken.None);
        }
        Assert.False(store.Connected);

        await client.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void NothingIsRegisteredInPublishMode()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--operation", "publish", "--output-path", Path.Combine(Path.GetTempPath(), "intochat-bridge-publish"), "--IntoChatOS:Topology=local", "--IntoChatOS:Shell:Enabled=false"],
            DisableDashboard = true,
        });
        var os = builder.AddIntoChatOS("Modules");
        os.AddKernel(DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel"));

        Assert.DoesNotContain(builder.Services, s => s.ServiceType == typeof(AspireBridgeEndpoint));
        Assert.DoesNotContain(builder.Services, s => s.ImplementationType == typeof(AspireBridgeServer));
    }
}
