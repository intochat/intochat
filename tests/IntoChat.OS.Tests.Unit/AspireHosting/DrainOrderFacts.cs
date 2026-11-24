using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.ServiceDefaults;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DrainOrderFacts
{
    [Fact]
    public async Task TheDrainStopsTheServerInStoppingAsyncBeforeAnyHostedServiceStops()
    {
        using var home = new DigitalBrain.Testing.TempDirectory("intochat-drain-");
        var ct = TestContext.Current.CancellationToken;
        var events = new List<string>();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.HomeKey] = home.Path,
            [HealthProbes.DrainDelayKey] = "00:00:00.010",
        });
        // Registered before the defaults, so it stops after the drain's own service; a later one stops before it.
        builder.Services.AddSingleton<IHostedService>(new Recorder("early", events));
        builder.AddServiceDefaults();
        builder.Services.AddSingleton<IServer>(new RecordingServer(events));
        builder.Services.AddSingleton<IHostedService>(new Recorder("late", events));
        using (var host = builder.Build())
        {
            await host.StartAsync(ct);
            await host.StopAsync(ct);
        }

        var firstStop = events.FindIndex(entry => entry.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.True(events.IndexOf("server-stop") is var stopped and >= 0 && stopped < firstStop, string.Join(", ", events));
        Assert.True(events.IndexOf("Stopping:late") < firstStop);
        Assert.True(events.IndexOf("Stopping:early") < firstStop);
    }

    private sealed class Recorder(string name, List<string> events) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) { lock (events) { events.Add("Stopping:" + name); } return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { lock (events) { events.Add("Stop:" + name); } return Task.CompletedTask; }
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingServer(List<string> events) : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { lock (events) { events.Add("server-stop"); } return Task.CompletedTask; }
        public void Dispose() { }
    }
}
